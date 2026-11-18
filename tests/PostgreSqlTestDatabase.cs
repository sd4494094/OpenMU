// <copyright file="PostgreSqlTestDatabase.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Testing;

using System.IO;
using System.Net;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence.EntityFramework;
using MUnique.OpenMU.Persistence.EntityFramework.Json;
using MUnique.OpenMU.PlugIns;
using Npgsql;

/// <summary>
/// Owns a unique PostgreSQL role and its disposable databases, including retained snapshot restore databases.
/// </summary>
/// <remarks>
/// Tests using this scope must be non-parallel because the connection provider and JSON converter registry are global.
/// The explicit maintenance connection must point to a disposable local cluster and its postgres database.
/// </remarks>
internal sealed class PostgreSqlTestDatabase : IAsyncDisposable
{
    private readonly string _maintenance;
    private readonly string _role = "openmu_test_" + Guid.NewGuid().ToString("N");
    private readonly string _database = "openmu_test_" + Guid.NewGuid().ToString("N");
    private readonly JsonConverter[] _previousConverters = JsonConverterRegistry.Converters.ToArray();
    private long _roleOid;
    private bool _snapshotRestoreGrant;
    private IDisposable? _settings;

    private PostgreSqlTestDatabase(string maintenance)
    {
        this._maintenance = maintenance;
    }

    /// <summary>
    /// Creates a database of its own and temporarily configures all persistence contexts to use it.
    /// </summary>
    /// <param name="initializeConfiguration">Whether to migrate and seed Season 6 configuration before returning.</param>
    /// <param name="allowSnapshotRestore">Whether to grant the temporary role permission to disable foreign key checks during snapshot import.</param>
    /// <returns>The scope which cleans up the role and its databases.</returns>
    internal static async ValueTask<PostgreSqlTestDatabase> CreateAsync(bool initializeConfiguration = false, bool allowSnapshotRestore = false)
    {
        var configured = Environment.GetEnvironmentVariable("OPENMU_INTEGRATION_TEST_CONNECTION");
        Assert.That(configured, Is.Not.Null.And.Not.Empty, "Select this test explicitly and set OPENMU_INTEGRATION_TEST_CONNECTION to a disposable local cluster's postgres database.");
        var builder = new NpgsqlConnectionStringBuilder(configured) { Pooling = false, Enlist = false, Timeout = 15, CommandTimeout = 120 };
        Assert.That(builder.Database, Is.EqualTo("postgres"), "A maintenance connection, rather than an application database, is required.");
        Assert.That(builder.Host is { } host && (Path.IsPathFullyQualified(host) || host == "localhost" || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address))), Is.True, "Only a local disposable PostgreSQL cluster is supported.");
        var scope = new PostgreSqlTestDatabase(builder.ConnectionString);
        try
        {
            var password = Guid.NewGuid().ToString("N");
            await using var maintenance = new NpgsqlConnection(scope._maintenance);
            await maintenance.OpenAsync().ConfigureAwait(false);
            await using (var createRole = new NpgsqlCommand($"CREATE ROLE {Quote(scope._role)} LOGIN CREATEDB PASSWORD '{password}'", maintenance))
            {
                await createRole.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await using (var identity = new NpgsqlCommand("SELECT oid::bigint FROM pg_roles WHERE rolname = @role", maintenance))
            {
                identity.Parameters.AddWithValue("role", scope._role);
                scope._roleOid = (long)(await identity.ExecuteScalarAsync().ConfigureAwait(false))!;
            }

            if (allowSnapshotRestore)
            {
                await using var grant = new NpgsqlCommand($"GRANT SET ON PARAMETER session_replication_role TO {Quote(scope._role)}", maintenance);
                await grant.ExecuteNonQueryAsync().ConfigureAwait(false);
                scope._snapshotRestoreGrant = true;
            }

            await using (var createDatabase = new NpgsqlCommand($"CREATE DATABASE {Quote(scope._database)} WITH TEMPLATE template0 OWNER {Quote(scope._role)}", maintenance))
            {
                await createDatabase.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            builder.Database = scope._database;
            builder.Username = scope._role;
            builder.Password = password;

            // The schema migrations also read the historical User Id spelling from the string.
            var connection = builder.ConnectionString.Replace("Username=", "User Id=", StringComparison.Ordinal) + ";";
            scope._settings = ConnectionConfigurator.UseTemporarily(new TestSettings(connection));
            JsonConverterRegistry.ClearConverters();
            JsonConverterRegistry.RegisterConverter(new LocalizedStringJsonConverter());
            JsonConverterRegistry.RegisterConverter(new BinaryAsHexJsonConverter());
            TestContext.Progress.WriteLine($"Created test database {scope._database}, owned by role {scope._role} (OID {scope._roleOid}).");
            if (initializeConfiguration)
            {
                var provider = new PersistenceContextProvider(NullLoggerFactory.Instance, null);
                using var initialization = await provider.ReCreateDatabaseAsync().ConfigureAwait(false);
                await new Persistence.Initialization.VersionSeasonSix.DataInitialization(provider, NullLoggerFactory.Instance).CreateInitialDataAsync(1, false).ConfigureAwait(false);
            }

            return scope;
        }
        catch
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        this._settings?.Dispose();
        this._settings = null;
        JsonConverterRegistry.ClearConverters();
        foreach (var converter in this._previousConverters)
        {
            JsonConverterRegistry.RegisterConverter(converter);
        }

        if (this._roleOid == 0)
        {
            return;
        }

        await using var maintenance = new NpgsqlConnection(this._maintenance);
        await maintenance.OpenAsync().ConfigureAwait(false);
        await using (var identity = new NpgsqlCommand("SELECT rolname FROM pg_roles WHERE oid::bigint = @oid", maintenance))
        {
            identity.Parameters.AddWithValue("oid", this._roleOid);
            Assert.That(await identity.ExecuteScalarAsync().ConfigureAwait(false), Is.EqualTo(this._role));
        }

        var owned = new List<(string Name, long Oid)>();
        await using (var inventory = new NpgsqlCommand("SELECT datname, oid::bigint FROM pg_database WHERE datdba::bigint = @owner ORDER BY datname", maintenance))
        {
            inventory.Parameters.AddWithValue("owner", this._roleOid);
            await using var reader = await inventory.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                owned.Add((reader.GetString(0), reader.GetInt64(1)));
            }
        }

        foreach (var (name, oid) in owned)
        {
            Assert.That(name == this._database || name.StartsWith("openmu_previous_", StringComparison.Ordinal) || name.StartsWith("openmu_restore_", StringComparison.Ordinal), Is.True, "An unexpected database is owned by the test role.");
            await using (var identity = new NpgsqlCommand("SELECT datdba::bigint FROM pg_database WHERE datname = @name AND oid::bigint = @oid", maintenance))
            {
                identity.Parameters.AddWithValue("name", name);
                identity.Parameters.AddWithValue("oid", oid);
                Assert.That(await identity.ExecuteScalarAsync().ConfigureAwait(false), Is.EqualTo(this._roleOid));
            }

            await using var drop = new NpgsqlCommand($"DROP DATABASE {Quote(name)} WITH (FORCE)", maintenance);
            await drop.ExecuteNonQueryAsync().ConfigureAwait(false);
            TestContext.Progress.WriteLine($"Dropped owned test database {name} (OID {oid}).");
        }

        if (this._snapshotRestoreGrant)
        {
            await using var revoke = new NpgsqlCommand($"REVOKE SET ON PARAMETER session_replication_role FROM {Quote(this._role)}", maintenance);
            await revoke.ExecuteNonQueryAsync().ConfigureAwait(false);
            this._snapshotRestoreGrant = false;
        }

        await using var dropRole = new NpgsqlCommand($"DROP ROLE {Quote(this._role)}", maintenance);
        await dropRole.ExecuteNonQueryAsync().ConfigureAwait(false);
        TestContext.Progress.WriteLine($"Dropped test role {this._role} (OID {this._roleOid}).");
        this._roleOid = 0;
    }

    private static string Quote(string identifier) => new NpgsqlCommandBuilder().QuoteIdentifier(identifier);

    private sealed class TestSettings(string connection) : IDatabaseConnectionSettingProvider
    {
        public Task? Initialization => Task.CompletedTask;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ConnectionSetting GetConnectionSetting<TContext>()
            where TContext : DbContext => this.GetConnectionSetting(typeof(TContext));

        public ConnectionSetting GetConnectionSetting(Type contextType) => new() { ConnectionString = connection, DatabaseEngine = DatabaseEngine.Npgsql };
    }
}
