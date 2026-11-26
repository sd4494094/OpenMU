// <copyright file="DatabaseSnapshotService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.EntityFramework;

using System.Buffers.Binary;
using System.Data;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using MUnique.OpenMU.Persistence.EntityFramework.AdminAuth;
using Npgsql;

/// <summary>
/// Implementation of the <see cref="IDatabaseSnapshotService"/> which uses the COPY command of postgres.
/// Each table is written into its own entry of a zip archive, in the binary format of postgres.
/// The archive also contains a manifest with the applied database migrations, so that a snapshot of
/// an older server can be restored: its schema is created first, and the migrations which came
/// afterwards are applied to the restored data.
/// </summary>
public class DatabaseSnapshotService : IDatabaseSnapshotService
{
    /// <summary>
    /// The name of the archive entry which describes the snapshot.
    /// </summary>
    private const string ManifestEntryName = "manifest.json";

    /// <summary>
    /// The version of the snapshot format. It's increased when the layout of the archive changes.
    /// </summary>
    private const int CurrentFormatVersion = 2;

    private const string TableEntryExtension = ".bin";

    private static readonly string[] IncludedSchemas =
    [
        SchemaNames.Configuration,
        SchemaNames.AccountData,
        SchemaNames.Guild,
        SchemaNames.Friend,
        SchemaNames.AdminPanel,
    ];

    /// <inheritdoc />
    public async Task CreateSnapshotAsync(Stream outputStream, CancellationToken cancellationToken = default)
    {
        await using var connection = await CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
        EnsureSameDatabase(connection.ConnectionString);

        // The table inventory, both migration histories and every COPY must see the
        // same committed state. Separate contexts/connections would create torn backups.
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection))
        {
            await readOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using var archive = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true);

        var tables = await GetTableNamesAsync(connection, cancellationToken).ConfigureAwait(false);
        var manifest = new SnapshotManifest(
            CurrentFormatVersion,
            DateTime.UtcNow,
            await GetAppliedMigrationsAsync<EntityDataContext>(connection, transaction, cancellationToken).ConfigureAwait(false),
            await GetAppliedMigrationsAsync<AdminPanelContext>(connection, transaction, cancellationToken).ConfigureAwait(false),
            tables.Select(table => table.ToString()).ToArray());

        var manifestEntry = archive.CreateEntry(ManifestEntryName);
        await using (var manifestStream = manifestEntry.Open())
        {
            await JsonSerializer.SerializeAsync(manifestStream, manifest, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.CreateEntry($"{table}{TableEntryExtension}");
            await using var entryStream = entry.Open();
            await using var copyStream = await connection
                .BeginRawBinaryCopyAsync($"COPY {table.ToQuotedString()} TO STDOUT (FORMAT BINARY)", cancellationToken)
                .ConfigureAwait(false);
            await copyStream.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<string?> GetRestoreBlockingReasonAsync(Stream inputStream, CancellationToken cancellationToken = default)
    {
        var previousPosition = inputStream.Position;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var archive = new ZipArchive(inputStream, ZipArchiveMode.Read, leaveOpen: true);
            var manifest = await ReadValidatedManifestAsync(archive, cancellationToken).ConfigureAwait(false);

            return GetIncompatibilityReason<EntityDataContext>(manifest.Migrations, "game database")
                   ?? GetIncompatibilityReason<AdminPanelContext>(manifest.AdminPanelMigrations, "admin panel database");
        }
        catch (InvalidDataException exception)
        {
            return exception.Message;
        }
        finally
        {
            inputStream.Position = previousPosition;
        }
    }

    /// <inheritdoc />
    public async Task RestoreSnapshotAsync(Stream inputStream, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var archive = new ZipArchive(inputStream, ZipArchiveMode.Read, leaveOpen: true);

        // The public restore entry point must not rely on callers having run the UI preflight.
        // Read every table payload before any destructive database operation.
        var manifest = await ReadValidatedManifestAsync(archive, cancellationToken).ConfigureAwait(false);

        if ((GetIncompatibilityReason<EntityDataContext>(manifest.Migrations, "game database")
             ?? GetIncompatibilityReason<AdminPanelContext>(manifest.AdminPanelMigrations, "admin panel database")) is { } incompatibility)
        {
            throw new InvalidOperationException(incompatibility);
        }

        // Contexts keep their normal provider configuration, but only this operation's
        // instances receive the isolated connection string. Never mutate global settings.
        await using var context = new EntityDataContext();
        var connectionString = context.Database.GetConnectionString()!;
        EnsureSameDatabase(connectionString);

        await StagedDatabaseRestore.RunAsync(
            connectionString,
            (stagedConnection, token) => RestoreIntoDatabaseAsync(archive, manifest, stagedConnection, token),
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask RestoreIntoDatabaseAsync(ZipArchive archive, SnapshotManifest manifest, string connectionString, CancellationToken cancellationToken)
    {
        // Recreate the snapshot's schema first, then migrate its actual restored data.
        await MigrateToSnapshotStateAsync<EntityDataContext>(manifest.Migrations, connectionString, cancellationToken).ConfigureAwait(false);
        await MigrateToSnapshotStateAsync<AdminPanelContext>(manifest.AdminPanelMigrations, connectionString, cancellationToken).ConfigureAwait(false);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var existingTables = (await GetTableNamesAsync(connection, cancellationToken).ConfigureAwait(false))
            .Select(table => table.ToString())
            .ToHashSet(StringComparer.Ordinal);
        if (!existingTables.SetEquals(manifest.Tables))
        {
            throw new InvalidDataException("The snapshot table list does not match its migration schema. Missing table data cannot be restored safely.");
        }

        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            // Tables reference each other in circles. Recheck all foreign keys after COPY,
            // because re-enabling the triggers alone does not validate imported rows.
            await DisableForeignKeyChecksAsync(connection, cancellationToken).ConfigureAwait(false);
            foreach (var tableName in manifest.Tables)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var table = TableName.Parse(tableName);
                await using var entryStream = archive.GetEntry($"{tableName}{TableEntryExtension}")!.Open();
                await using var copyStream = await connection
                    .BeginRawBinaryCopyAsync($"COPY {table.ToQuotedString()} FROM STDIN (FORMAT BINARY)", cancellationToken)
                    .ConfigureAwait(false);
                await entryStream.CopyToAsync(copyStream, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await ValidateForeignKeysAsync(connection, cancellationToken).ConfigureAwait(false);
        await connection.CloseAsync().ConfigureAwait(false);
        await MigrateToAsync<EntityDataContext>(null, connectionString, cancellationToken).ConfigureAwait(false);
        await MigrateToAsync<AdminPanelContext>(null, connectionString, cancellationToken).ConfigureAwait(false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ValidateForeignKeysAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ValidateForeignKeysAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var constraints = new List<(string Table, string Definition)>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT format('%I.%I', n.nspname, r.relname), pg_get_constraintdef(c.oid)
            FROM pg_constraint c JOIN pg_class r ON r.oid = c.conrelid
            JOIN pg_namespace n ON n.oid = r.relnamespace
            WHERE c.contype = 'f' AND n.nspname = ANY(@schemas)
            """,
            connection))
        {
            command.Parameters.AddWithValue("schemas", IncludedSchemas);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                constraints.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var (table, definition) in constraints)
        {
            var name = "openmu_restore_check_" + Guid.NewGuid().ToString("N");
            var foreignKey = definition.EndsWith(" NOT VALID", StringComparison.Ordinal) ? definition[..^10] : definition;
            await using var command = new NpgsqlCommand(
                $"""
                ALTER TABLE {table} ADD CONSTRAINT "{name}" {foreignKey} NOT VALID;
                ALTER TABLE {table} VALIDATE CONSTRAINT "{name}";
                ALTER TABLE {table} DROP CONSTRAINT "{name}";
                """,
                connection);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Determines why a snapshot with the given migrations can't be restored by this server.
    /// The snapshot may be older than this server - the migrations which came later are applied
    /// to the restored data then. It can't be newer, because we don't know its schema.
    /// </summary>
    /// <typeparam name="TContext">The type of the database context.</typeparam>
    /// <param name="snapshotMigrations">The migrations which were applied when the snapshot was created.</param>
    /// <param name="databaseName">The name of the database, for the message.</param>
    /// <returns>The reason why it can't be restored; <c>null</c>, if it can be restored.</returns>
    private static string? GetIncompatibilityReason<TContext>(string[] snapshotMigrations, string databaseName)
        where TContext : DbContext, new()
    {
        using var context = new TContext();
        var knownMigrations = context.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var unknownMigrations = snapshotMigrations.Where(migration => !knownMigrations.Contains(migration)).ToList();
        if (unknownMigrations.Count > 0)
        {
            return $"The snapshot of the {databaseName} was created by a newer or different version of the server: "
                   + $"it contains the unknown database migration '{unknownMigrations[0]}'. "
                   + "Please use the data backup (json) to transfer the data.";
        }

        return null;
    }

    /// <summary>
    /// Creates the database schema of the moment when the snapshot was created.
    /// </summary>
    /// <typeparam name="TContext">The type of the database context.</typeparam>
    /// <param name="snapshotMigrations">The migrations which were applied when the snapshot was created.</param>
    /// <param name="connectionString">The isolated restore database connection string.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private static async Task MigrateToSnapshotStateAsync<TContext>(string[] snapshotMigrations, string connectionString, CancellationToken cancellationToken)
        where TContext : DbContext, new()
    {
        if (snapshotMigrations.Length == 0)
        {
            // This database didn't exist when the snapshot was created; it's created below, when
            // the remaining migrations are applied.
            return;
        }

        await using var context = new TContext();

        // The migrations are applied in the order of their identifier. When a migration was added
        // later with an earlier identifier - which happens when branches are merged - it's applied
        // here, too. That's not a problem as long as it doesn't change a table of the snapshot;
        // otherwise the copy of that table fails, and the restore is rolled back.
        var target = context.Database.GetMigrations().Last(snapshotMigrations.Contains);
        await MigrateToAsync<TContext>(target, connectionString, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Migrates the schema of the given context to the given migration.
    /// </summary>
    /// <typeparam name="TContext">The type of the database context.</typeparam>
    /// <param name="targetMigration">The migration which should be the last applied one; <c>null</c>, to apply all of them.</param>
    /// <param name="connectionString">The isolated restore database connection string.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private static async Task MigrateToAsync<TContext>(string? targetMigration, string connectionString, CancellationToken cancellationToken)
        where TContext : DbContext, new()
    {
        await using var context = new TContext();
        context.Database.SetConnectionString(connectionString);
        if (targetMigration is null && !context.Database.GetMigrations().Any())
        {
            return;
        }

        await context.Database.GetService<IMigrator>()
            .MigrateAsync(targetMigration, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<NpgsqlConnection> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        // Creating the context also ensures that the connection settings are initialized.
        await using var context = new EntityDataContext();
        var connection = new NpgsqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task DisableForeignKeyChecksAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SET LOCAL session_replication_role = replica";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            throw new InvalidOperationException(
                "The database user needs superuser rights to restore a snapshot, because the foreign key checks "
                + "have to be disabled while the data is inserted. Please use the data backup (json) instead.",
                exception);
        }
    }

    private static async ValueTask<SnapshotManifest?> ReadManifestAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        if (archive.GetEntry(ManifestEntryName) is not { } entry)
        {
            return null;
        }

        await using var stream = entry.Open();
        return await JsonSerializer.DeserializeAsync<SnapshotManifest>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<SnapshotManifest> ReadValidatedManifestAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (!entries.Add(entry.FullName))
            {
                throw new InvalidDataException($"The snapshot contains the duplicate entry '{entry.FullName}'.");
            }
        }

        SnapshotManifest manifest;
        try
        {
            manifest = await ReadManifestAsync(archive, cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidDataException("The selected file is no database snapshot.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The snapshot manifest contains invalid JSON.", exception);
        }

        if (manifest.FormatVersion != CurrentFormatVersion)
        {
            throw new InvalidDataException($"The snapshot was created in format version {manifest.FormatVersion}, but this server expects version {CurrentFormatVersion}.");
        }

        if (manifest.Tables is null || manifest.Migrations is null || manifest.AdminPanelMigrations is null
            || manifest.Migrations.Any(string.IsNullOrWhiteSpace) || manifest.AdminPanelMigrations.Any(string.IsNullOrWhiteSpace)
            || manifest.Migrations.Distinct(StringComparer.Ordinal).Count() != manifest.Migrations.Length
            || manifest.AdminPanelMigrations.Distinct(StringComparer.Ordinal).Count() != manifest.AdminPanelMigrations.Length)
        {
            throw new InvalidDataException("The snapshot manifest must contain valid table and migration lists.");
        }

        var expectedEntries = new HashSet<string>(StringComparer.Ordinal) { ManifestEntryName };
        foreach (var name in manifest.Tables)
        {
            var separator = name?.IndexOf('.', StringComparison.Ordinal) ?? -1;
            if (separator <= 0 || separator == name!.Length - 1 || name.IndexOf('.', separator + 1) >= 0
                || !IncludedSchemas.Contains(name[..separator], StringComparer.Ordinal)
                || string.IsNullOrWhiteSpace(name[(separator + 1)..]) || name[(separator + 1)..] == "__EFMigrationsHistory")
            {
                throw new InvalidDataException("The snapshot manifest contains an invalid table name or schema.");
            }

            if (!expectedEntries.Add(name + TableEntryExtension))
            {
                throw new InvalidDataException($"The snapshot manifest lists table '{name}' more than once.");
            }

            if (!entries.Contains(name + TableEntryExtension))
            {
                throw new InvalidDataException($"The archive doesn't contain the data of table '{name}', which is listed in its manifest.");
            }
        }

        if (!entries.SetEquals(expectedEntries))
        {
            throw new InvalidDataException("The snapshot contains entries which are not listed in its manifest.");
        }

        foreach (var name in manifest.Tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ValidateTableDataAsync(archive.GetEntry(name + TableEntryExtension)!, cancellationToken).ConfigureAwait(false);
        }

        return manifest;
    }

    private static async ValueTask ValidateTableDataAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        // PostgreSQL binary COPY: signature, flags, extension, tuples, then a -1 tuple count.
        // Validate framing with bounded memory. PostgreSQL still validates field types during restore.
        const int headerLength = 19;
        var buffer = new byte[81920];
        await using var stream = entry.Open();
        var remaining = entry.Length;
        try
        {
            await ReadAsync(headerLength).ConfigureAwait(false);
            if (!buffer.AsSpan(0, 11).SequenceEqual(Convert.FromHexString("5047434F50590AFF0D0A00"))
                || BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(11, 4)) != 0)
            {
                throw new InvalidDataException("Invalid or unsupported PostgreSQL COPY header.");
            }

            var extensionLength = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(15, 4));
            await SkipAsync(extensionLength).ConfigureAwait(false);
            short? expectedFields = null;
            while (true)
            {
                await ReadAsync(2).ConfigureAwait(false);
                var fields = BinaryPrimitives.ReadInt16BigEndian(buffer);
                if (fields == -1)
                {
                    if (remaining != 0 || await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
                    {
                        throw new InvalidDataException("Unexpected data after the PostgreSQL COPY trailer.");
                    }

                    break;
                }

                if (fields < 0 || (expectedFields.HasValue && fields != expectedFields))
                {
                    throw new InvalidDataException("Invalid PostgreSQL COPY tuple field count.");
                }

                expectedFields = fields;
                for (var field = 0; field < fields; field++)
                {
                    await ReadAsync(4).ConfigureAwait(false);
                    var length = BinaryPrimitives.ReadInt32BigEndian(buffer);
                    if (length != -1)
                    {
                        await SkipAsync(length).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (IOException exception)
        {
            throw new InvalidDataException($"The snapshot data of table '{entry.FullName}' is invalid or incomplete.", exception);
        }

        async ValueTask ReadAsync(int length)
        {
            if (length < 0 || length > remaining)
            {
                throw new InvalidDataException("Invalid or truncated PostgreSQL COPY data.");
            }

            await stream.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            remaining -= length;
        }

        async ValueTask SkipAsync(int length)
        {
            if (length < 0 || length > remaining)
            {
                throw new InvalidDataException("Invalid PostgreSQL COPY field or header extension length.");
            }

            while (length > 0)
            {
                var count = Math.Min(length, buffer.Length);
                await ReadAsync(count).ConfigureAwait(false);
                length -= count;
            }
        }
    }

    private static async ValueTask<System.Collections.Generic.List<TableName>> GetTableNamesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        // The migration history is not part of the snapshot - the re-created database brings its own.
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT table_schema, table_name
            FROM information_schema.tables
            WHERE table_schema = ANY(@schemas)
              AND table_type = 'BASE TABLE'
              AND table_name <> '__EFMigrationsHistory'
            ORDER BY table_schema, table_name
            """;
        command.Parameters.AddWithValue("schemas", IncludedSchemas);

        var result = new System.Collections.Generic.List<TableName>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new TableName(reader.GetString(0), reader.GetString(1)));
        }

        return result;
    }

    private static void EnsureSameDatabase(string connectionString)
    {
        var game = new NpgsqlConnectionStringBuilder(connectionString);
        using var context = new AdminPanelContext();
        var admin = new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString());
        if (game.Host != admin.Host || game.Port != admin.Port || game.Database != admin.Database)
        {
            throw new InvalidOperationException("Game and admin snapshot data must belong to the same PostgreSQL database.");
        }
    }

    private static async ValueTask<string[]> GetAppliedMigrationsAsync<TContext>(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
        where TContext : DbContext, new()
    {
        await using var context = new TContext();
        context.Database.SetDbConnection(connection, contextOwnsConnection: false);
        await context.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);

        // Npgsql optimistically reads the history and catches a missing-table error.
        // Such an error still aborts a PostgreSQL transaction, so check the correctly
        // quoted, search-path-aware name without raising that error in the first place.
        var options = context.GetService<IDbContextOptions>().Extensions.OfType<RelationalOptionsExtension>().Single();
        var historyName = context.GetService<ISqlGenerationHelper>().DelimitIdentifier(
            options.MigrationsHistoryTableName ?? HistoryRepository.DefaultTableName,
            options.MigrationsHistoryTableSchema);
        await using var exists = new NpgsqlCommand("SELECT pg_catalog.to_regclass(@name) IS NOT NULL", connection);
        exists.Parameters.AddWithValue("name", historyName);
        if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        {
            return [];
        }

        var applied = await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false);

        // Preserve migration order. Permission, invalid-column and connection errors
        // must propagate instead of reporting an apparently successful partial backup.
        var knownMigrations = context.Database.GetMigrations().ToList();
        return knownMigrations.Where(applied.Contains).ToArray();
    }

    /// <summary>
    /// The description of a snapshot, so that we can check if it fits to the current database.
    /// </summary>
    /// <param name="FormatVersion">The version of the snapshot format.</param>
    /// <param name="CreatedAt">The point in time when the snapshot was created.</param>
    /// <param name="Migrations">The migrations of the game database which were applied when the snapshot was created.</param>
    /// <param name="AdminPanelMigrations">The migrations of the admin panel database which were applied when the snapshot was created.</param>
    /// <param name="Tables">The names of the tables which are contained in the snapshot.</param>
    private sealed record SnapshotManifest(int FormatVersion, DateTime CreatedAt, string[] Migrations, string[] AdminPanelMigrations, string[] Tables);

    /// <summary>
    /// The name of a table of the database.
    /// </summary>
    /// <param name="Schema">The name of the schema.</param>
    /// <param name="Name">The name of the table.</param>
    private sealed record TableName(string Schema, string Name)
    {
        /// <summary>
        /// Parses a table name of the format "schema.table".
        /// </summary>
        /// <param name="value">The value which should be parsed.</param>
        /// <returns>The parsed table name.</returns>
        public static TableName Parse(string value)
        {
            var separatorIndex = value.IndexOf('.', StringComparison.Ordinal);
            return separatorIndex < 0
                ? throw new ArgumentException($"'{value}' is no valid table name.", nameof(value))
                : new TableName(value[..separatorIndex], value[(separatorIndex + 1)..]);
        }

        /// <inheritdoc />
        public override string ToString() => $"{this.Schema}.{this.Name}";

        /// <summary>
        /// Gets the quoted name which can be used in a sql statement.
        /// </summary>
        /// <returns>The quoted name.</returns>
        public string ToQuotedString() => $"{Quote(this.Schema)}.{Quote(this.Name)}";

        private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
