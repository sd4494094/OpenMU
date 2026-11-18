// <copyright file="DatabaseSnapshotConsistencyTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using MUnique.OpenMU.Persistence.EntityFramework;
using MUnique.OpenMU.Persistence.EntityFramework.AdminAuth;
using Npgsql;

/// <summary>
/// Exercises real snapshot creation with concurrent commits on newly created disposable databases.
/// </summary>
[TestFixture]
[Explicit("Requires OPENMU_SNAPSHOT_TEST_CONNECTION pointing to a disposable PostgreSQL cluster.")]
[NonParallelizable]
public class DatabaseSnapshotConsistencyTests
{
    private string _maintenance = null!;
    private string _database = null!;
    private string _connection = null!;
    private long _oid;
    private IDisposable? _settings;
    private string[] _migrations = null!;
    private string[] _adminMigrations = null!;

    /// <summary>
    /// Creates artificial data and migration histories without touching an existing application database.
    /// </summary>
    [SetUp]
    public async Task SetUpAsync()
    {
        var configured = Environment.GetEnvironmentVariable("OPENMU_SNAPSHOT_TEST_CONNECTION");
        Assert.That(configured, Is.Not.Null.And.Not.Empty);
        var settings = new NpgsqlConnectionStringBuilder(configured) { Database = "postgres", Pooling = false, Enlist = false };
        this._maintenance = settings.ConnectionString;
        this._database = "openmu_snapshot_test_" + Guid.NewGuid().ToString("N");
        await this.ExecuteAsync(this._maintenance, $"CREATE DATABASE \"{this._database}\" WITH TEMPLATE template0").ConfigureAwait(false);
        settings.Database = this._database;
        settings.ApplicationName = this._database;
        this._connection = settings.ConnectionString;
        this._oid = (long)(await this.ScalarAsync(this._maintenance, $"SELECT oid::bigint FROM pg_database WHERE datname = '{this._database}'").ConfigureAwait(false))!;
        this._settings = ConnectionConfigurator.UseTemporarily(new SnapshotSettings(this._connection));
        using var context = new EntityDataContext();
        this._migrations = context.Database.GetMigrations().Take(2).ToArray();
        using var admin = new AdminPanelContext();
        this._adminMigrations = admin.Database.GetMigrations().Take(2).ToArray();
        await this.ExecuteAsync(this._connection, """
            CREATE SCHEMA config;
            CREATE SCHEMA data;
            CREATE SCHEMA admin;
            CREATE TABLE config."SnapshotFirst" (generation integer NOT NULL, payload bytea NOT NULL);
            CREATE TABLE data."SnapshotSecond" (generation integer NOT NULL);
            INSERT INTO data."SnapshotSecond" VALUES (1);
            CREATE TABLE public."__EFMigrationsHistory" ("MigrationId" varchar(150) PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL);
            CREATE TABLE admin."__EFMigrationsHistory" ("MigrationId" varchar(150) PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL);
            """).ConfigureAwait(false);
        await using var connection = new NpgsqlConnection(this._connection);
        await connection.OpenAsync().ConfigureAwait(false);
        var payload = new byte[262144];
        Random.Shared.NextBytes(payload);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO config."SnapshotFirst" VALUES (1, @payload);
            INSERT INTO public."__EFMigrationsHistory" VALUES (@game, '10.0.2');
            INSERT INTO admin."__EFMigrationsHistory" VALUES (@admin, '10.0.2');
            """, connection);
        insert.Parameters.AddWithValue("payload", payload);
        insert.Parameters.AddWithValue("game", this._migrations[0]);
        insert.Parameters.AddWithValue("admin", this._adminMigrations[0]);
        await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Restores the global test settings and drops only this test's recorded database identity.
    /// </summary>
    [TearDown]
    public async Task TearDownAsync()
    {
        this._settings?.Dispose();
        if (this._oid == 0)
        {
            return;
        }

        Assert.That(await this.ScalarAsync(this._maintenance, $"SELECT datname FROM pg_database WHERE oid::bigint = {this._oid}").ConfigureAwait(false), Is.EqualTo(this._database));
        await this.ExecuteAsync(this._maintenance, $"DROP DATABASE \"{this._database}\" WITH (FORCE)").ConfigureAwait(false);
        this._oid = 0;
    }

    /// <summary>
    /// A transaction updating both tables midway through the first COPY cannot split their versions.
    /// </summary>
    [Test]
    public async Task ConcurrentWritesProduceOneSnapshotAsync()
    {
        using var output = new SnapshotOutputGate();
        var task = new DatabaseSnapshotService().CreateSnapshotAsync(output);
        try
        {
            await output.Entered.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            await this.ExecuteAsync(this._connection, """
                BEGIN;
                UPDATE config."SnapshotFirst" SET generation = 2;
                UPDATE data."SnapshotSecond" SET generation = 2;
                COMMIT;
                """).ConfigureAwait(false);
        }
        finally
        {
            output.Release();
            await task.ConfigureAwait(false);
        }

        Assert.That(await this.ScalarAsync(this._connection, "SELECT generation FROM data.\"SnapshotSecond\"").ConfigureAwait(false), Is.EqualTo(2));
        using var archive = new ZipArchive(new MemoryStream(output.ToArray()), ZipArchiveMode.Read);
        Assert.That(ReadGeneration(archive, "config.SnapshotFirst.bin"), Is.EqualTo(1));
        Assert.That(ReadGeneration(archive, "data.SnapshotSecond.bin"), Is.EqualTo(1), "Both exported tables must precede the same committed writer transaction.");
    }

    /// <summary>
    /// Migration reads delayed by a concurrent transaction must use the table inventory's snapshot too.
    /// </summary>
    [Test]
    public async Task MigrationHistoryAndDataShareSnapshotAsync()
    {
        await using var writer = new NpgsqlConnection(this._connection);
        await writer.OpenAsync().ConfigureAwait(false);
        await using var transaction = await writer.BeginTransactionAsync().ConfigureAwait(false);
        await using (var block = new NpgsqlCommand("LOCK TABLE public.\"__EFMigrationsHistory\" IN ACCESS EXCLUSIVE MODE", writer))
        {
            await block.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        using var output = new MemoryStream();
        var task = new DatabaseSnapshotService().CreateSnapshotAsync(output);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (await this.ScalarAsync(this._maintenance, $"SELECT count(*) FROM pg_stat_activity WHERE datname = '{this._database}' AND wait_event_type = 'Lock' AND query LIKE '%MigrationId%'").ConfigureAwait(false) is not 1L)
            {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "Snapshot creation must actually reach the locked migration history.");
                await Task.Delay(20).ConfigureAwait(false);
            }

            await using var change = new NpgsqlCommand("""
                UPDATE config."SnapshotFirst" SET generation = 2;
                UPDATE data."SnapshotSecond" SET generation = 2;
                INSERT INTO public."__EFMigrationsHistory" VALUES (@game, '10.0.2');
                INSERT INTO admin."__EFMigrationsHistory" VALUES (@admin, '10.0.2');
                """, writer);
            change.Parameters.AddWithValue("game", this._migrations[1]);
            change.Parameters.AddWithValue("admin", this._adminMigrations[1]);
            await change.ExecuteNonQueryAsync().ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
        }
        finally
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            await task.ConfigureAwait(false);
        }

        using var archive = new ZipArchive(new MemoryStream(output.ToArray()), ZipArchiveMode.Read);
        using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
        Assert.That(manifest.RootElement.GetProperty("Migrations").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(this._migrations.Take(1)));
        Assert.That(manifest.RootElement.GetProperty("AdminPanelMigrations").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(this._adminMigrations.Take(1)));
        Assert.That(ReadGeneration(archive, "config.SnapshotFirst.bin"), Is.EqualTo(1));
        Assert.That(ReadGeneration(archive, "data.SnapshotSecond.bin"), Is.EqualTo(1));
    }

    /// <summary>
    /// An output failure or cancellation releases the read transaction and database connection.
    /// </summary>
    /// <param name="cancel">Whether to cancel instead of failing the output stream.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async Task InterruptedOutputReleasesDatabaseAsync(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        using var output = new SnapshotOutputGate { FailAfterGate = !cancel };
        var task = new DatabaseSnapshotService().CreateSnapshotAsync(output, cancellation.Token);
        await output.Entered.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (cancel)
        {
            cancellation.Cancel();
        }

        output.Release();
        try
        {
            await task.ConfigureAwait(false);
            Assert.Fail("Interrupted snapshot creation must not report success.");
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            Assert.That(cancel ? error is OperationCanceledException : error is IOException, Is.True);
        }

        Assert.That(await this.ScalarAsync(this._maintenance, $"SELECT count(*) FROM pg_stat_activity WHERE datname = '{this._database}' AND state LIKE 'idle in transaction%'").ConfigureAwait(false), Is.EqualTo(0L));
        await this.ExecuteAsync(this._connection, "UPDATE data.\"SnapshotSecond\" SET generation = 3").ConfigureAwait(false);
        Assert.That(await this.ScalarAsync(this._connection, "SELECT generation FROM data.\"SnapshotSecond\"").ConfigureAwait(false), Is.EqualTo(3));
    }

    /// <summary>
    /// Missing histories before setup are valid; a broken existing history must fail loudly.
    /// </summary>
    /// <param name="broken">Whether the game history exists with an invalid column layout.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async Task MissingAndBrokenHistoriesAreDistinguishedAsync(bool broken)
    {
        await this.ExecuteAsync(this._connection, "DROP TABLE public.\"__EFMigrationsHistory\"; DROP TABLE admin.\"__EFMigrationsHistory\"").ConfigureAwait(false);
        if (broken)
        {
            await this.ExecuteAsync(this._connection, "CREATE TABLE public.\"__EFMigrationsHistory\" (wrong_column integer)").ConfigureAwait(false);
        }

        using var output = new MemoryStream();
        if (broken)
        {
            var error = Assert.ThrowsAsync<PostgresException>(async () => await new DatabaseSnapshotService().CreateSnapshotAsync(output).ConfigureAwait(false));
            Assert.That(error!.SqlState, Is.EqualTo("42703"));
            return;
        }

        await new DatabaseSnapshotService().CreateSnapshotAsync(output).ConfigureAwait(false);
        using var archive = new ZipArchive(new MemoryStream(output.ToArray()), ZipArchiveMode.Read);
        using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
        Assert.That(manifest.RootElement.GetProperty("Migrations").GetArrayLength(), Is.Zero);
        Assert.That(manifest.RootElement.GetProperty("AdminPanelMigrations").GetArrayLength(), Is.Zero);
        Assert.That(ReadGeneration(archive, "data.SnapshotSecond.bin"), Is.EqualTo(1));
    }

    /// <summary>
    /// Split game/admin configurations cannot silently produce a partial snapshot.
    /// </summary>
    [Test]
    public void SplitDatabaseConfigurationIsRejected()
    {
        var settings = new NpgsqlConnectionStringBuilder(this._connection) { Database = "postgres" };
        using var split = ConnectionConfigurator.UseTemporarily(new SnapshotSettings(this._connection, settings.ConnectionString));
        using var output = new MemoryStream();
        Assert.ThrowsAsync<InvalidOperationException>(async () => await new DatabaseSnapshotService().CreateSnapshotAsync(output).ConfigureAwait(false));
        Assert.That(output.Length, Is.Zero);
    }

    private static int ReadGeneration(ZipArchive archive, string entry)
    {
        using var stream = archive.GetEntry(entry)!.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        var row = 19 + BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(15));
        Assert.That(BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(row + 2)), Is.EqualTo(4));
        return BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(row + 6));
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

    private sealed class SnapshotSettings(string connectionString, string? adminConnection = null) : IDatabaseConnectionSettingProvider
    {
        public Task? Initialization => null;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ConnectionSetting GetConnectionSetting<TContextType>()
            where TContextType : DbContext => this.GetConnectionSetting(typeof(TContextType));

        public ConnectionSetting GetConnectionSetting(Type contextType) => new() { DatabaseEngine = DatabaseEngine.Npgsql, ConnectionString = contextType == typeof(AdminPanelContext) ? adminConnection ?? connectionString : connectionString };
    }

    private sealed class SnapshotOutputGate : MemoryStream
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _firstTableStarted;

        public Task Entered => this._entered.Task;

        public bool FailAfterGate { get; init; }

        public void Release() => this._release.TrySetResult();

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            this._firstTableStarted |= buffer.IndexOf("config.SnapshotFirst.bin"u8) >= 0;
            var bytes = buffer.ToArray();
            base.Write(bytes, 0, bytes.Length);
        }

        public override void Write(byte[] buffer, int offset, int count) => this.Write(buffer.AsSpan(offset, count));

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (this._firstTableStarted)
            {
                this._entered.TrySetResult();
                await this._release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (this.FailAfterGate)
                {
                    throw new IOException("Intentional snapshot output failure.");
                }
            }

            this.Write(buffer.Span);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => await this.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }
}
