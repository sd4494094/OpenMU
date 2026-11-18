// <copyright file="StagedDatabaseRestore.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.EntityFramework;

using System.Threading;
using Npgsql;

/// <summary>
/// Builds a replacement database before switching names in a PostgreSQL transaction.
/// The previous database is retained, with connections disabled, for operator recovery.
/// </summary>
internal static class StagedDatabaseRestore
{
    /// <summary>
    /// Restores into an isolated database and publishes it only after the restore succeeds.
    /// </summary>
    /// <param name="connectionString">The target database admin connection string.</param>
    /// <param name="restore">The import and migration operation, using the supplied isolated connection string.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the operation.</returns>
    internal static async ValueTask RunAsync(string connectionString, Func<string, CancellationToken, ValueTask> restore, CancellationToken cancellationToken)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, Enlist = false };
        var target = settings.Database;
        if (string.IsNullOrWhiteSpace(target) || target is "postgres" or "template0" or "template1")
        {
            throw new InvalidOperationException("Snapshot restore requires an explicitly named application database.");
        }

        var stage = "openmu_restore_" + Guid.NewGuid().ToString("N");
        var previous = "openmu_previous_" + Guid.NewGuid().ToString("N");
        settings.Database = "postgres";
        await using var maintenance = new NpgsqlConnection(settings.ConnectionString);
        await maintenance.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Serialize cooperating restore processes across the whole cluster, without changing
        // the process-wide connection provider or holding a transaction during the import.
        await using (var guard = new NpgsqlCommand("SELECT pg_try_advisory_lock(1836412517, hashtext(@target))", maintenance))
        {
            guard.Parameters.AddWithValue("target", target);
            if (await guard.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                throw new InvalidOperationException("Another snapshot restore is already running for this database.");
            }
        }

        var originalOid = await GetOidAsync(maintenance, target, cancellationToken).ConfigureAwait(false);
        var stageOid = 0L;
        var targetConnectionsMayBeDisabled = false;
        Exception? restoreFailure = null;
        try
        {
            await CreateStageAsync(maintenance, target, stage, cancellationToken).ConfigureAwait(false);
            stageOid = await GetOidAsync(maintenance, stage, CancellationToken.None).ConfigureAwait(false);
            await ExecuteAsync(maintenance, $"REVOKE ALL ON DATABASE {Quote(stage)} FROM PUBLIC", cancellationToken).ConfigureAwait(false);
            settings.Database = stage;
            await restore(settings.ConnectionString, cancellationToken).ConfigureAwait(false);
            await CopyMetadataAsync(maintenance, target, stage, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (await GetOidAsync(maintenance, target, cancellationToken).ConfigureAwait(false) != originalOid
                || await GetOidAsync(maintenance, stage, cancellationToken).ConfigureAwait(false) != stageOid)
            {
                throw new InvalidOperationException("A database identity changed while the snapshot was being restored.");
            }

            // Closing only our stage connections is insufficient: game/admin pools may still
            // hold the target open. Block new sessions before ending the old ones. Failure
            // cleanup re-enables the original by OID; it never deletes the target name.
            await SetConnectionsAsync(maintenance, stage, false, cancellationToken).ConfigureAwait(false);
            if (originalOid != 0)
            {
                targetConnectionsMayBeDisabled = true;
                await SetConnectionsAsync(maintenance, target, false, cancellationToken).ConfigureAwait(false);
            }

            await using (var terminate = new NpgsqlCommand("SELECT pg_terminate_backend(pid, 5000) FROM pg_stat_activity WHERE datid IN (@original, @stage) AND pid <> pg_backend_pid()", maintenance))
            {
                terminate.Parameters.AddWithValue("original", NpgsqlTypes.NpgsqlDbType.Oid, (uint)originalOid);
                terminate.Parameters.AddWithValue("stage", NpgsqlTypes.NpgsqlDbType.Oid, (uint)stageOid);
                await terminate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var transaction = await maintenance.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            if (originalOid != 0)
            {
                await ExecuteAsync(maintenance, $"ALTER DATABASE {Quote(target)} RENAME TO {Quote(previous)}", cancellationToken).ConfigureAwait(false);
            }

            await ExecuteAsync(maintenance, $"ALTER DATABASE {Quote(stage)} RENAME TO {Quote(target)}", cancellationToken).ConfigureAwait(false);
            await SetConnectionsAsync(maintenance, target, true, cancellationToken).ConfigureAwait(false);

            // Cancellation is honored until the commit boundary. A network failure while
            // committing can still have an uncertain outcome, so cleanup uses identities,
            // never an assumption that an exception means the commit was rolled back.
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            restoreFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                // Use a new maintenance session even if the original one broke. Each cleanup
                // command has a bounded timeout and ignores caller cancellation deliberately.
                settings.Database = "postgres";
                settings.Timeout = 15;
                settings.CommandTimeout = 15;
                await using var cleanup = new NpgsqlConnection(settings.ConnectionString);
                await cleanup.OpenAsync(CancellationToken.None).ConfigureAwait(false);
                if (targetConnectionsMayBeDisabled)
                {
                    // Retire dead pooled sessions for every game/admin role. Busy sessions
                    // are discarded when returned, so they cannot keep writing the old OID.
                    NpgsqlConnection.ClearAllPools();
                }

                if (targetConnectionsMayBeDisabled && await GetOidAsync(cleanup, target, CancellationToken.None).ConfigureAwait(false) == originalOid)
                {
                    await SetConnectionsAsync(cleanup, target, true, CancellationToken.None).ConfigureAwait(false);
                }

                if (stageOid != 0 && await GetOidAsync(cleanup, stage, CancellationToken.None).ConfigureAwait(false) == stageOid)
                {
                    await ExecuteAsync(cleanup, $"DROP DATABASE {Quote(stage)} WITH (FORCE)", CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception cleanupFailure) when (restoreFailure is not null)
            {
                throw new AggregateException("Snapshot restore and staging cleanup both failed. Inspect the retained database identities before retrying.", restoreFailure, cleanupFailure);
            }
        }
    }

    private static async ValueTask CreateStageAsync(NpgsqlConnection connection, string target, string stage, CancellationToken cancellationToken)
    {
        // Retain database owner, encoding, collation provider and tablespace. template0
        // supplies an empty database; copying the live target would copy its data too.
        await using var definition = new NpgsqlCommand(
            """
            SELECT CASE WHEN datallowconn AND NOT datistemplate THEN
                format('CREATE DATABASE %I WITH TEMPLATE template0 OWNER %I ENCODING %L LC_COLLATE %L LC_CTYPE %L',
                    @stage, pg_get_userbyid(datdba), pg_encoding_to_char(encoding), datcollate, datctype)
                -- Omitting TABLESPACE inherits template0's location without requiring
                -- an extra CREATE grant. Other locations still need explicit permission.
                || CASE WHEN d.dattablespace = (SELECT dattablespace FROM pg_database WHERE datname = 'template0')
                    THEN '' ELSE format(' TABLESPACE %I', t.spcname) END
                || CASE to_jsonb(d)->>'datlocprovider'
                    WHEN 'i' THEN format(' LOCALE_PROVIDER icu ICU_LOCALE %L', coalesce(to_jsonb(d)->>'datlocale', to_jsonb(d)->>'daticulocale'))
                    WHEN 'b' THEN format(' LOCALE_PROVIDER builtin BUILTIN_LOCALE %L', to_jsonb(d)->>'datlocale')
                    WHEN 'c' THEN ' LOCALE_PROVIDER libc'
                    ELSE '' END
                ELSE '' END
            FROM pg_database d JOIN pg_tablespace t ON t.oid = d.dattablespace WHERE datname = @target
            """,
            connection);
        definition.Parameters.AddWithValue("stage", stage);
        definition.Parameters.AddWithValue("target", target);
        var create = await definition.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (create == string.Empty)
        {
            throw new InvalidOperationException("A template or connection-disabled database cannot be replaced by a snapshot.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await ExecuteAsync(connection, create ?? $"CREATE DATABASE {Quote(stage)} WITH TEMPLATE template0", CancellationToken.None).ConfigureAwait(false);
    }

    private static async ValueTask CopyMetadataAsync(NpgsqlConnection connection, string target, string stage, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, $"REVOKE ALL ON DATABASE {Quote(stage)} FROM PUBLIC", cancellationToken).ConfigureAwait(false);
        if (await GetOidAsync(connection, target, cancellationToken).ConfigureAwait(false) == 0)
        {
            // A new installation uses the normal PostgreSQL public connection defaults.
            await ExecuteAsync(connection, $"GRANT CONNECT, TEMPORARY ON DATABASE {Quote(stage)} TO PUBLIC", cancellationToken).ConfigureAwait(false);
            return;
        }

        // Generate quoted statements on the server, including role-specific database
        // settings. Never log setting values, which may contain private configuration.
        await using var metadata = new NpgsqlCommand(
            """
            SELECT format('GRANT %s ON DATABASE %I TO %s%s', privilege_type, @stage,
                CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE quote_ident(pg_get_userbyid(a.grantee)) END,
                CASE WHEN is_grantable THEN ' WITH GRANT OPTION' ELSE '' END)
            FROM pg_database d CROSS JOIN LATERAL aclexplode(coalesce(d.datacl, acldefault('d', d.datdba))) a WHERE datname = @target
            UNION ALL
            SELECT CASE WHEN setrole = 0
                THEN format('ALTER DATABASE %I SET %I TO %L', @stage, split_part(setting, '=', 1), substr(setting, strpos(setting, '=') + 1))
                ELSE format('ALTER ROLE %I IN DATABASE %I SET %I TO %L', pg_get_userbyid(setrole), @stage, split_part(setting, '=', 1), substr(setting, strpos(setting, '=') + 1)) END
            FROM pg_db_role_setting s JOIN pg_database d ON d.oid = s.setdatabase CROSS JOIN LATERAL unnest(setconfig) setting WHERE datname = @target
            UNION ALL
            SELECT format('ALTER DATABASE %I CONNECTION LIMIT %s', @stage, datconnlimit) FROM pg_database WHERE datname = @target
            """,
            connection);
        metadata.Parameters.AddWithValue("stage", stage);
        metadata.Parameters.AddWithValue("target", target);
        var statements = new List<string>();
        await using (var reader = await metadata.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                statements.Add(reader.GetString(0));
            }
        }

        foreach (var statement in statements)
        {
            await ExecuteAsync(connection, statement, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<long> GetOidAsync(NpgsqlConnection connection, string name, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT oid::bigint FROM pg_database WHERE datname = @name", connection);
        command.Parameters.AddWithValue("name", name);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long oid ? oid : 0;
    }

    private static ValueTask SetConnectionsAsync(NpgsqlConnection connection, string name, bool allow, CancellationToken cancellationToken)
        => ExecuteAsync(connection, $"ALTER DATABASE {Quote(name)} ALLOW_CONNECTIONS {(allow ? "true" : "false")}", cancellationToken);

    private static async ValueTask ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
