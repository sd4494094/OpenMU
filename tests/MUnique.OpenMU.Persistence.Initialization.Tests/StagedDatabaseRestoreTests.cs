// <copyright file="StagedDatabaseRestoreTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using System.Threading;
using MUnique.OpenMU.Persistence.EntityFramework;
using Npgsql;

/// <summary>
/// Explicit PostgreSQL integration tests, using only newly created database identities.
/// Set OPENMU_SNAPSHOT_TEST_CONNECTION to a disposable cluster's maintenance connection.
/// </summary>
[TestFixture]
[Explicit("Requires a disposable PostgreSQL cluster and database creation privileges.")]
[NonParallelizable]
public class StagedDatabaseRestoreTests
{
    private string _maintenance = null!;
    private string _target = null!;
    private string _connection = null!;
    private long _originalOid;
    private string? _stage;
    private long _stageOid;
    private string? _restoreRole;

    /// <summary>
    /// Creates a fresh target and records its identity before testing a replacement.
    /// </summary>
    [SetUp]
    public async Task SetUpAsync()
    {
        var configured = Environment.GetEnvironmentVariable("OPENMU_SNAPSHOT_TEST_CONNECTION");
        Assert.That(configured, Is.Not.Null.And.Not.Empty, "Explicit tests require an operator-supplied disposable cluster.");
        var settings = new NpgsqlConnectionStringBuilder(configured) { Database = "postgres", Pooling = false, Enlist = false };
        this._maintenance = settings.ConnectionString;
        this._target = "openmu_test_" + Guid.NewGuid().ToString("N");
        this._stage = null;
        this._stageOid = 0;
        this._restoreRole = null;
        await this.ExecuteAsync(this._maintenance, $"CREATE DATABASE {Quote(this._target)} WITH TEMPLATE template0").ConfigureAwait(false);
        settings.Database = this._target;
        this._connection = settings.ConnectionString;
        this._originalOid = (long)(await this.ScalarAsync(this._maintenance, $"SELECT oid::bigint FROM pg_database WHERE datname = '{this._target}'").ConfigureAwait(false))!;
        await this.ExecuteAsync(this._connection, "CREATE TABLE marker(value integer NOT NULL); INSERT INTO marker VALUES (17)").ConfigureAwait(false);
    }

    /// <summary>
    /// Removes only databases whose recorded OIDs belong to this test.
    /// </summary>
    [TearDown]
    public async Task TearDownAsync()
    {
        if (this._originalOid == 0)
        {
            return;
        }

        var names = new List<string>();
        await using (var connection = new NpgsqlConnection(this._maintenance))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = new NpgsqlCommand($"SELECT datname FROM pg_database WHERE oid::bigint IN ({this._originalOid}, {this._stageOid})", connection);
            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                names.Add(reader.GetString(0));
            }
        }

        foreach (var name in names)
        {
            Assert.That(name.StartsWith("openmu_test_", StringComparison.Ordinal) || name.StartsWith("openmu_previous_", StringComparison.Ordinal) || name == this._stage, Is.True);
            await this.ExecuteAsync(this._maintenance, $"DROP DATABASE {Quote(name)} WITH (FORCE)").ConfigureAwait(false);
        }

        if (this._restoreRole is { } role)
        {
            await this.ExecuteAsync(this._maintenance, $"DROP ROLE {Quote(role)}").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Import or cancellation failures leave the original name, identity and data intact.
    /// </summary>
    /// <param name="cancel">Whether to cancel instead of simulating an import failure.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async Task FailedImportPreservesOriginalAsync(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        try
        {
            await StagedDatabaseRestore.RunAsync(
                this._connection,
                async (staged, token) =>
                {
                    await this.RecordStageAsync(staged).ConfigureAwait(false);
                    await this.ExecuteAsync(staged, "CREATE TABLE marker(value integer); INSERT INTO marker VALUES (99)").ConfigureAwait(false);
                    if (cancel)
                    {
                        cancellation.Cancel();
                        token.ThrowIfCancellationRequested();
                    }

                    throw new InvalidOperationException("Simulated failure after partial staged import.");
                },
                cancellation.Token).ConfigureAwait(false);
            Assert.Fail("A failed import must not publish a replacement.");
        }
        catch (Exception error) when (error is InvalidOperationException or OperationCanceledException)
        {
            Assert.That(cancel ? error is OperationCanceledException : error is InvalidOperationException, Is.True);
        }

        await this.AssertOriginalAsync().ConfigureAwait(false);
        Assert.That(await this.ScalarAsync(this._maintenance, $"SELECT count(*) FROM pg_database WHERE oid::bigint = {this._stageOid}").ConfigureAwait(false), Is.EqualTo(0L));
    }

    /// <summary>
    /// Successful switching closes existing sessions, retains the old identity and settings,
    /// and makes new connections reach the imported data.
    /// </summary>
    [Test]
    public async Task SuccessfulImportRetainsPreviousAndSettingsAsync()
    {
        await this.ExecuteAsync(this._maintenance, $"ALTER DATABASE {Quote(this._target)} SET application_name TO 'snapshot retained setting'; REVOKE TEMPORARY ON DATABASE {Quote(this._target)} FROM PUBLIC").ConfigureAwait(false);
        await using var existing = new NpgsqlConnection(this._connection);
        await existing.OpenAsync().ConfigureAwait(false);
        await StagedDatabaseRestore.RunAsync(
            this._connection,
            async (staged, _) =>
            {
                await this.RecordStageAsync(staged).ConfigureAwait(false);
                Assert.That(await this.ScalarAsync(this._connection, "SELECT value FROM marker").ConfigureAwait(false), Is.EqualTo(17));
                await this.ExecuteAsync(staged, "CREATE TABLE marker(value integer NOT NULL); INSERT INTO marker VALUES (99)").ConfigureAwait(false);
            },
            CancellationToken.None).ConfigureAwait(false);

        Assert.That(await this.ScalarAsync(this._connection, "SELECT value FROM marker").ConfigureAwait(false), Is.EqualTo(99));
        Assert.That(await this.ScalarAsync(this._connection, "SHOW application_name").ConfigureAwait(false), Is.EqualTo("snapshot retained setting"));
        Assert.That(await this.ScalarAsync(this._maintenance, $"SELECT oid::bigint FROM pg_database WHERE datname = '{this._target}'").ConfigureAwait(false), Is.EqualTo(this._stageOid));
        var previous = (string)(await this.ScalarAsync(this._maintenance, $"SELECT datname FROM pg_database WHERE oid::bigint = {this._originalOid} AND NOT datallowconn").ConfigureAwait(false))!;
        Assert.That(previous, Does.StartWith("openmu_previous_"));
        Assert.That(await this.ScalarAsync(this._maintenance, $"SELECT EXISTS(SELECT 1 FROM pg_database d CROSS JOIN LATERAL aclexplode(coalesce(d.datacl, acldefault('d',d.datdba))) a WHERE datname = '{this._target}' AND grantee = 0 AND privilege_type = 'TEMPORARY')").ConfigureAwait(false), Is.False);
        await this.ExecuteAsync(this._maintenance, $"ALTER DATABASE {Quote(previous)} ALLOW_CONNECTIONS true").ConfigureAwait(false);
        var settings = new NpgsqlConnectionStringBuilder(this._connection) { Database = previous };
        Assert.That(await this.ScalarAsync(settings.ConnectionString, "SELECT value FROM marker").ConfigureAwait(false), Is.EqualTo(17));
    }

    /// <summary>
    /// Competing restores for one target fail without touching either database.
    /// </summary>
    [Test]
    public async Task ConcurrentRestoreIsRejectedAsync()
    {
        await StagedDatabaseRestore.RunAsync(
            this._connection,
            async (staged, _) =>
            {
                await this.RecordStageAsync(staged).ConfigureAwait(false);
                Assert.ThrowsAsync<InvalidOperationException>(async () => await StagedDatabaseRestore.RunAsync(this._connection, (_, _) => ValueTask.CompletedTask, CancellationToken.None).ConfigureAwait(false));
                await this.ExecuteAsync(staged, "CREATE TABLE marker(value integer); INSERT INTO marker VALUES (99)").ConfigureAwait(false);
            },
            CancellationToken.None).ConfigureAwait(false);
        Assert.That(await this.ScalarAsync(this._connection, "SELECT value FROM marker").ConfigureAwait(false), Is.EqualTo(99));
    }

    /// <summary>
    /// A new installation can publish an imported database without an existing target.
    /// </summary>
    [Test]
    public async Task MissingTargetCanBeCreatedAsync()
    {
        await this.ExecuteAsync(this._maintenance, $"DROP DATABASE {Quote(this._target)}").ConfigureAwait(false);
        await StagedDatabaseRestore.RunAsync(
            this._connection,
            async (staged, _) =>
            {
                await this.RecordStageAsync(staged).ConfigureAwait(false);
                await this.ExecuteAsync(staged, "CREATE TABLE marker(value integer); INSERT INTO marker VALUES (99)").ConfigureAwait(false);
            },
            CancellationToken.None).ConfigureAwait(false);
        Assert.That(await this.ScalarAsync(this._connection, "SELECT value FROM marker").ConfigureAwait(false), Is.EqualTo(99));
        Assert.That(await this.ScalarAsync(this._maintenance, $"SELECT count(*) FROM pg_database WHERE oid::bigint = {this._originalOid}").ConfigureAwait(false), Is.EqualTo(0L));
    }

    /// <summary>
    /// PostgreSQL can complete the rename while its automatic maintenance worker is active.
    /// A restore database owner does not need permission to terminate that worker.
    /// </summary>
    /// <param name="vacuumStage">Whether to start the worker on the staging database instead of the target.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async Task AutovacuumDoesNotBlockLimitedOwnerAsync(bool vacuumStage)
    {
        var limited = await this.CreateLimitedOwnerAsync().ConfigureAwait(false);
        await StagedDatabaseRestore.RunAsync(
            limited,
            async (staged, _) =>
            {
                await this.RecordStageAsync(staged).ConfigureAwait(false);
                await this.ExecuteAsync(staged, "CREATE TABLE marker(value integer); INSERT INTO marker VALUES (99)").ConfigureAwait(false);
                var database = vacuumStage ? this._stage! : this._target;
                await this.StartAutovacuumAsync(vacuumStage ? staged : limited, database).ConfigureAwait(false);
            },
            CancellationToken.None).ConfigureAwait(false);

        Assert.That(await this.ScalarAsync(this._connection, "SELECT value FROM marker").ConfigureAwait(false), Is.EqualTo(99));
        Assert.That(await this.ScalarAsync(this._maintenance, $"SELECT oid::bigint FROM pg_database WHERE datname = '{this._target}' AND datallowconn").ConfigureAwait(false), Is.EqualTo(this._stageOid));
        Assert.That(await this.ScalarAsync(this._maintenance, $"SELECT datname FROM pg_database WHERE oid::bigint = {this._originalOid} AND NOT datallowconn").ConfigureAwait(false), Does.StartWith("openmu_previous_"));
    }

    /// <summary>
    /// A failed import removes its staging database even during automatic maintenance.
    /// </summary>
    [Test]
    public async Task FailedImportCleansStageWithAutovacuumAsync()
    {
        var limited = await this.CreateLimitedOwnerAsync().ConfigureAwait(false);
        var importFailure = new InvalidOperationException("Simulated failure after the staging worker starts.");
        var failure = Assert.ThrowsAsync<InvalidOperationException>(async () => await StagedDatabaseRestore.RunAsync(
            limited,
            async (staged, _) =>
            {
                await this.RecordStageAsync(staged).ConfigureAwait(false);
                await this.StartAutovacuumAsync(staged, this._stage!).ConfigureAwait(false);
                throw importFailure;
            },
            CancellationToken.None).ConfigureAwait(false));

        Assert.That(failure, Is.SameAs(importFailure));
        await this.AssertOriginalAsync().ConfigureAwait(false);
        Assert.That(await this.ScalarAsync(this._maintenance, $"SELECT count(*) FROM pg_database WHERE oid::bigint = {this._stageOid}").ConfigureAwait(false), Is.EqualTo(0L));
    }

    /// <summary>
    /// Restoring as a limited owner leaves a protected client session and the original database available.
    /// </summary>
    [Test]
    public async Task ProtectedClientSessionPreventsReplacementAsync()
    {
        var limited = await this.CreateLimitedOwnerAsync().ConfigureAwait(false);
        await using var existing = new NpgsqlConnection(this._connection);
        await existing.OpenAsync().ConfigureAwait(false);
        var failure = Assert.ThrowsAsync<PostgresException>(async () => await StagedDatabaseRestore.RunAsync(
            limited,
            async (staged, _) =>
            {
                await this.RecordStageAsync(staged).ConfigureAwait(false);
                await this.ExecuteAsync(staged, "CREATE TABLE marker(value integer); INSERT INTO marker VALUES (99)").ConfigureAwait(false);
            },
            CancellationToken.None).ConfigureAwait(false));

        Assert.That(failure!.SqlState, Is.EqualTo(PostgresErrorCodes.InsufficientPrivilege));
        await using var original = new NpgsqlCommand("SELECT value FROM marker", existing);
        Assert.That(await original.ExecuteScalarAsync().ConfigureAwait(false), Is.EqualTo(17));
        await this.AssertOriginalAsync().ConfigureAwait(false);
        Assert.That(await this.ScalarAsync(this._maintenance, $"SELECT count(*) FROM pg_database WHERE oid::bigint = {this._stageOid}").ConfigureAwait(false), Is.EqualTo(0L));
    }

    /// <summary>
    /// A prepared transaction forces the second rename to fail after the first one ran.
    /// Both names must roll back, and cleanup errors must not hide the restore error.
    /// </summary>
    [Test]
    public async Task SecondRenameFailurePreservesOriginalAsync()
    {
        if ((string)(await this.ScalarAsync(this._maintenance, "SHOW max_prepared_transactions").ConfigureAwait(false))! == "0")
        {
            Assert.Ignore("This fault-injection test requires prepared transactions in the disposable cluster.");
        }

        var prepared = "openmu_test_" + Guid.NewGuid().ToString("N");
        string? stageConnection = null;
        var preparedCreated = false;
        try
        {
            var failure = Assert.ThrowsAsync<AggregateException>(async () => await StagedDatabaseRestore.RunAsync(
                this._connection,
                async (staged, _) =>
                {
                    stageConnection = staged;
                    await this.RecordStageAsync(staged).ConfigureAwait(false);
                    await this.ExecuteAsync(staged, "CREATE TABLE marker(value integer); INSERT INTO marker VALUES (99)").ConfigureAwait(false);
                    await this.ExecuteAsync(staged, $"BEGIN; INSERT INTO marker VALUES (100); PREPARE TRANSACTION '{prepared}'").ConfigureAwait(false);
                    preparedCreated = true;
                },
                CancellationToken.None).ConfigureAwait(false));
            Assert.That(failure!.InnerExceptions, Has.Count.EqualTo(2));
            Assert.That(failure.InnerExceptions[0], Is.TypeOf<PostgresException>());
            Assert.That(((PostgresException)failure.InnerExceptions[0]).SqlState, Is.EqualTo("55006"));
            await this.AssertOriginalAsync().ConfigureAwait(false);
            Assert.That(await this.ScalarAsync(this._maintenance, $"SELECT count(*) FROM pg_database WHERE oid::bigint = {this._stageOid} AND datname = '{this._stage}'").ConfigureAwait(false), Is.EqualTo(1L));
        }
        finally
        {
            if (preparedCreated)
            {
                await this.ExecuteAsync(this._maintenance, $"ALTER DATABASE {Quote(this._stage!)} ALLOW_CONNECTIONS true").ConfigureAwait(false);
                await this.ExecuteAsync(stageConnection!, $"ROLLBACK PREPARED '{prepared}'").ConfigureAwait(false);
            }
        }
    }

    private static string Quote(string name) => '"' + name.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    private async ValueTask<string> CreateLimitedOwnerAsync()
    {
        var role = "openmu_test_restore_" + Guid.NewGuid().ToString("N");
        await this.ExecuteAsync(this._maintenance, $"CREATE ROLE {Quote(role)} LOGIN CREATEDB").ConfigureAwait(false);
        this._restoreRole = role;
        await this.ExecuteAsync(this._maintenance, $"ALTER DATABASE {Quote(this._target)} OWNER TO {Quote(role)}").ConfigureAwait(false);
        var settings = new NpgsqlConnectionStringBuilder(this._connection) { Username = role, Password = string.Empty };
        return settings.ConnectionString;
    }

    private async ValueTask StartAutovacuumAsync(string connection, string database)
    {
        if ((string?)await this.ScalarAsync(this._maintenance, "SHOW autovacuum").ConfigureAwait(false) != "on")
        {
            Assert.Ignore("This scenario needs autovacuum enabled on the disposable cluster.");
        }

        // Give the worker enough table pages to remain active while restore reaches the rename.
        await this.ExecuteAsync(
            connection,
            """
            CREATE TABLE vacuum_probe (value integer, padding text) WITH (
                autovacuum_vacuum_insert_threshold = -1,
                autovacuum_analyze_threshold = 1000000,
                autovacuum_vacuum_threshold = 1,
                autovacuum_vacuum_scale_factor = 0,
                autovacuum_vacuum_cost_limit = 1,
                autovacuum_vacuum_cost_delay = 50
            );
            INSERT INTO vacuum_probe SELECT value, repeat('x', 512) FROM generate_series(1, 40000) value;
            DELETE FROM vacuum_probe WHERE value > 1
            """).ConfigureAwait(false);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        await using var maintenance = new NpgsqlConnection(this._maintenance);
        await maintenance.OpenAsync(timeout.Token).ConfigureAwait(false);
        await using var worker = new NpgsqlCommand(
            "SELECT EXISTS (SELECT FROM pg_stat_activity WHERE datname = @database AND backend_type = 'autovacuum worker')",
            maintenance);
        worker.Parameters.AddWithValue("database", database);
        while (await worker.ExecuteScalarAsync(timeout.Token).ConfigureAwait(false) is not true)
        {
            await Task.Delay(250, timeout.Token).ConfigureAwait(false);
        }
    }

    private async ValueTask RecordStageAsync(string connectionString)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        this._stage = settings.Database;
        Assert.That(settings.Pooling, Is.False);
        Assert.That(this._stage, Is.Not.EqualTo(this._target));
        this._stageOid = (long)(await this.ScalarAsync(this._maintenance, $"SELECT oid::bigint FROM pg_database WHERE datname = '{this._stage}'").ConfigureAwait(false))!;
    }

    private async ValueTask AssertOriginalAsync()
    {
        Assert.That(await this.ScalarAsync(this._maintenance, $"SELECT oid::bigint FROM pg_database WHERE datname = '{this._target}' AND datallowconn").ConfigureAwait(false), Is.EqualTo(this._originalOid));
        Assert.That(await this.ScalarAsync(this._connection, "SELECT value FROM marker").ConfigureAwait(false), Is.EqualTo(17));
    }

    private async ValueTask<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync().ConfigureAwait(false);
    }

    private async ValueTask ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
