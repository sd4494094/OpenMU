// <copyright file="DefaultTablespaceRestoreTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using System.Threading;
using MUnique.OpenMU.Persistence.EntityFramework;
using MUnique.OpenMU.Testing;
using Npgsql;

/// <summary>
/// Verifies staged restore with database creation privileges and no explicit tablespace grant.
/// </summary>
[TestFixture]
[Explicit("Requires OPENMU_INTEGRATION_TEST_CONNECTION pointing to a disposable local PostgreSQL cluster.")]
[NonParallelizable]
public class DefaultTablespaceRestoreTests
{
    /// <summary>
    /// Restores a database on template0's tablespace without requiring a redundant tablespace CREATE grant.
    /// </summary>
    /// <returns>The async task.</returns>
    [Test]
    public async Task RestoreOnTemplateTablespaceRequiresOnlyCreateDatabaseAsync()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync().ConfigureAwait(false);
        var connection = ConnectionConfigurator.GetConnectionString<EntityDataContext>();
        Assert.That(await ScalarAsync(connection, """
            SELECT rolcreatedb AND NOT rolsuper
                AND NOT has_tablespace_privilege((SELECT dattablespace FROM pg_database WHERE datname = current_database()), 'CREATE')
            FROM pg_roles WHERE rolname = current_user
            """).ConfigureAwait(false), Is.True, "The test must exercise a non-superuser without an explicit tablespace grant.");
        var metadataSql = """
            SELECT json_build_object('owner', datdba::bigint, 'tablespace', dattablespace::bigint,
                'encoding', encoding, 'collate', datcollate, 'ctype', datctype, 'limit', datconnlimit)::text
            FROM pg_database WHERE datname = current_database()
            """;
        var metadata = await ScalarAsync(connection, metadataSql).ConfigureAwait(false);
        Assert.That(metadata, Is.Not.Null);
        var originalOid = (long)(await ScalarAsync(connection, "SELECT oid::bigint FROM pg_database WHERE datname = current_database()").ConfigureAwait(false))!;
        await ExecuteAsync(connection, "CREATE TABLE restore_marker(value integer NOT NULL); INSERT INTO restore_marker VALUES (17)", CancellationToken.None).ConfigureAwait(false);

        await StagedDatabaseRestore.RunAsync(
            connection,
            async (stage, cancellationToken) => await ExecuteAsync(stage, "CREATE TABLE restore_marker(value integer NOT NULL); INSERT INTO restore_marker VALUES (29)", cancellationToken).ConfigureAwait(false),
            CancellationToken.None).ConfigureAwait(false);

        Assert.That(await ScalarAsync(connection, metadataSql).ConfigureAwait(false), Is.EqualTo(metadata), "Owner, tablespace and database settings must be retained.");
        Assert.That(await ScalarAsync(connection, "SELECT value FROM restore_marker").ConfigureAwait(false), Is.EqualTo(29));
        Assert.That(await ScalarAsync(connection, "SELECT oid::bigint FROM pg_database WHERE datname = current_database()").ConfigureAwait(false), Is.Not.EqualTo(originalOid));
        Assert.That(await ScalarAsync(connection, $"SELECT datname LIKE 'openmu_previous_%' AND NOT datallowconn FROM pg_database WHERE oid::bigint = {originalOid}").ConfigureAwait(false), Is.True, "The original database must be retained with connections disabled.");
    }

    /// <summary>
    /// A custom tablespace is preserved when permitted and cannot be bypassed when its CREATE grant is absent.
    /// </summary>
    /// <param name="grantCreate">Whether the test role may create databases on the custom tablespace.</param>
    /// <returns>The async task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task CustomTablespaceRequiresItsOwnCreateGrantAsync(bool grantCreate)
    {
        var tablespace = Environment.GetEnvironmentVariable("OPENMU_INTEGRATION_TEST_TABLESPACE");
        Assert.That(tablespace, Is.Not.Null.And.Not.Empty, "Supply a disposable custom tablespace in the integration cluster.");
        Assert.That(tablespace, Is.Not.EqualTo("pg_default").And.Not.EqualTo("pg_global"));
        var maintenance = Environment.GetEnvironmentVariable("OPENMU_INTEGRATION_TEST_CONNECTION")!;
        await using var database = await PostgreSqlTestDatabase.CreateAsync().ConfigureAwait(false);
        var connection = ConnectionConfigurator.GetConnectionString<EntityDataContext>();
        var settings = new NpgsqlConnectionStringBuilder(connection);
        var role = Quote(settings.Username!);
        var target = Quote(settings.Database!);
        var location = Quote(tablespace!);
        var originalOid = await ScalarAsync(connection, "SELECT oid::bigint FROM pg_database WHERE datname = current_database()").ConfigureAwait(false);
        await ExecuteAsync(connection, "CREATE TABLE restore_marker(value integer NOT NULL); INSERT INTO restore_marker VALUES (17)", CancellationToken.None).ConfigureAwait(false);
        await ExecuteAsync(maintenance, $"ALTER DATABASE {target} SET TABLESPACE {location}", CancellationToken.None).ConfigureAwait(false);
        var callbackInvoked = false;
        try
        {
            if (grantCreate)
            {
                await ExecuteAsync(maintenance, $"GRANT CREATE ON TABLESPACE {location} TO {role}", CancellationToken.None).ConfigureAwait(false);
            }

            Assert.That(await ScalarAsync(connection, "SELECT has_tablespace_privilege((SELECT dattablespace FROM pg_database WHERE datname = current_database()), 'CREATE')").ConfigureAwait(false), Is.EqualTo(grantCreate));
            async ValueTask RestoreAsync(string stage, CancellationToken cancellationToken)
            {
                callbackInvoked = true;
                await ExecuteAsync(stage, "CREATE TABLE restore_marker(value integer NOT NULL); INSERT INTO restore_marker VALUES (29)", cancellationToken).ConfigureAwait(false);
            }

            if (grantCreate)
            {
                await StagedDatabaseRestore.RunAsync(connection, RestoreAsync, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                var error = Assert.ThrowsAsync<PostgresException>(async () => await StagedDatabaseRestore.RunAsync(connection, RestoreAsync, CancellationToken.None).ConfigureAwait(false));
                Assert.That(error!.SqlState, Is.EqualTo(PostgresErrorCodes.InsufficientPrivilege));
            }

            Assert.That(callbackInvoked, Is.EqualTo(grantCreate));
            Assert.That(await ScalarAsync(connection, "SELECT value FROM restore_marker").ConfigureAwait(false), Is.EqualTo(grantCreate ? 29 : 17));
            Assert.That(await ScalarAsync(connection, "SELECT t.spcname FROM pg_database d JOIN pg_tablespace t ON t.oid = d.dattablespace WHERE datname = current_database()").ConfigureAwait(false), Is.EqualTo(tablespace));
            var currentOid = await ScalarAsync(connection, "SELECT oid::bigint FROM pg_database WHERE datname = current_database()").ConfigureAwait(false);
            Assert.That(Equals(currentOid, originalOid), Is.EqualTo(!grantCreate), "Only a successful restore may replace the original database identity.");
        }
        finally
        {
            await ExecuteAsync(maintenance, $"REVOKE CREATE ON TABLESPACE {location} FROM {role}", CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static string Quote(string identifier) => new NpgsqlCommandBuilder().QuoteIdentifier(identifier);

    private static async ValueTask<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync().ConfigureAwait(false);
    }

    private static async ValueTask ExecuteAsync(string connectionString, string sql, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
