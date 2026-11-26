// <copyright file="CashShopPurchaseDatabaseTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.EntityFramework;
using Npgsql;
using AccountEntity = MUnique.OpenMU.Persistence.EntityFramework.Model.Account;
using StorageEntity = MUnique.OpenMU.Persistence.EntityFramework.Model.CashShopStorageItem;

/// <summary>
/// Exercises cash shop capacity, account locking and rollback with independent PostgreSQL sessions.
/// </summary>
[TestFixture]
[Explicit("Requires OPENMU_CASH_TEST_CONNECTION pointing to a disposable PostgreSQL cluster.")]
[NonParallelizable]
public class CashShopPurchaseDatabaseTest
{
    private string _maintenance = null!;
    private string _database = null!;
    private string _connection = null!;
    private long _oid;
    private IDisposable? _settings;

    /// <summary>
    /// Creates a disposable database with the actual account and cash storage column mappings.
    /// </summary>
    /// <returns>The task.</returns>
    [SetUp]
    public async ValueTask SetUpAsync()
    {
        var configured = Environment.GetEnvironmentVariable("OPENMU_CASH_TEST_CONNECTION");
        Assert.That(configured, Is.Not.Null.And.Not.Empty);
        var builder = new NpgsqlConnectionStringBuilder(configured) { Database = "postgres", Pooling = false };
        this._maintenance = builder.ConnectionString;
        this._database = "openmu_cash_test_" + Guid.NewGuid().ToString("N");
        await this.ExecuteAsync(this._maintenance, $"CREATE DATABASE \"{this._database}\" WITH TEMPLATE template0").ConfigureAwait(false);
        builder.Database = this._database;
        this._connection = builder.ConnectionString;
        await using var connection = new NpgsqlConnection(this._maintenance);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT oid::bigint FROM pg_database WHERE datname = @name", connection);
        command.Parameters.AddWithValue("name", this._database);
        this._oid = (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
        this._settings = ConnectionConfigurator.UseTemporarily(new CashSettings(this._connection));
        await using var context = new AccountContext();
        await this.ExecuteAsync(this._connection, "CREATE SCHEMA data").ConfigureAwait(false);
        foreach (var type in new[] { typeof(AccountEntity), typeof(StorageEntity) })
        {
            var entity = context.Model.FindEntityType(type)!;
            var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
            var columns = entity.GetProperties().Select(property =>
                $"\"{property.GetColumnName(table)}\" {property.GetRelationalTypeMapping().StoreType}" +
                (property.IsPrimaryKey() ? " PRIMARY KEY" : string.Empty));
            await this.ExecuteAsync(this._connection, $"CREATE TABLE data.\"{entity.GetTableName()}\" ({string.Join(", ", columns)})").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Drops only the database whose identity was recorded during setup.
    /// </summary>
    /// <returns>The task.</returns>
    [TearDown]
    public async ValueTask TearDownAsync()
    {
        this._settings?.Dispose();
        if (this._oid == 0)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(this._maintenance);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT datname FROM pg_database WHERE oid::bigint = @oid", connection);
        command.Parameters.AddWithValue("oid", this._oid);
        Assert.That(await command.ExecuteScalarAsync().ConfigureAwait(false), Is.EqualTo(this._database));
        await this.ExecuteAsync(this._maintenance, $"DROP DATABASE \"{this._database}\" WITH (FORCE)").ConfigureAwait(false);
        this._oid = 0;
    }

    /// <summary>
    /// Two senders on separate PostgreSQL sessions compete for the recipient's last slot.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async ValueTask ConcurrentPurchasesRespectCapacityAsync()
    {
        var accounts = await this.CreateAccountsAsync(3).ConfigureAwait(false);
        using var first = this.CreateContext();
        using var second = this.CreateContext();
        var firstAccount = await first.Context.Set<AccountEntity>().SingleAsync(account => account.Id == accounts[0]).ConfigureAwait(false);
        var secondAccount = await second.Context.Set<AccountEntity>().SingleAsync(account => account.Id == accounts[1]).ConfigureAwait(false);
        var pids = await Task.WhenAll(this.GetBackendIdAsync(first).AsTask(), this.GetBackendIdAsync(second).AsTask()).ConfigureAwait(false);
        Assert.That(pids[0], Is.Not.EqualTo(pids[1]), "The requests must use different live PostgreSQL sessions.");
        TestContext.WriteLine($"Independent PostgreSQL backends: {pids[0]}, {pids[1]}");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        async Task<bool> PurchaseAsync(PlayerContext context, AccountEntity account)
        {
            await release.Task.ConfigureAwait(false);
            return await context.TrySaveCashShopPurchaseAsync(account.Id, accounts[2], 1, 1, () =>
            {
                Interlocked.Increment(ref callbacks);
                AddPurchase(context, account, accounts[2]);
            }).ConfigureAwait(false);
        }

        var tasks = new[] { PurchaseAsync(first, firstAccount), PurchaseAsync(second, secondAccount) };
        release.TrySetResult();
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        Assert.That(results, Is.EquivalentTo(new[] { true, false }));
        Assert.That(callbacks, Is.EqualTo(1));
        await using var reloaded = new AccountContext();
        Assert.That(await reloaded.Set<StorageEntity>().CountAsync().ConfigureAwait(false), Is.EqualTo(1));
        var balances = await reloaded.Set<AccountEntity>().Where(account => account.Id != accounts[2]).Select(account => account.WCoinC).ToListAsync().ConfigureAwait(false);
        Assert.That(balances, Is.EquivalentTo(new[] { 100, 80 }));
    }

    /// <summary>
    /// Reciprocal gifts lock the same account pair in the same order and both commit.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async ValueTask ReciprocalGiftsDoNotDeadlockAsync()
    {
        var accounts = await this.CreateAccountsAsync(2).ConfigureAwait(false);
        using var first = this.CreateContext();
        using var second = this.CreateContext();
        var a = await first.Context.Set<AccountEntity>().SingleAsync(account => account.Id == accounts[0]).ConfigureAwait(false);
        var b = await second.Context.Set<AccountEntity>().SingleAsync(account => account.Id == accounts[1]).ConfigureAwait(false);
        var results = await Task.WhenAll(
            first.TrySaveCashShopPurchaseAsync(a.Id, b.Id, 1, 1, () => AddPurchase(first, a, b.Id)).AsTask(),
            second.TrySaveCashShopPurchaseAsync(b.Id, a.Id, 1, 1, () => AddPurchase(second, b, a.Id)).AsTask())
            .WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        Assert.That(results, Is.All.True);
        await using var reloaded = new AccountContext();
        Assert.That(await reloaded.Set<StorageEntity>().CountAsync().ConfigureAwait(false), Is.EqualTo(2));
        Assert.That(await reloaded.Set<AccountEntity>().Select(account => account.WCoinC).ToListAsync().ConfigureAwait(false), Is.All.EqualTo(80));
    }

    /// <summary>
    /// A rejected database write commits neither coins nor storage and leaves the context recoverable.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async ValueTask RejectedSaveRollsBackCoinsAndStorageAsync()
    {
        var accounts = await this.CreateAccountsAsync(2).ConfigureAwait(false);
        await this.ExecuteAsync(this._connection, "ALTER TABLE data.\"CashShopStorageItem\" ADD CONSTRAINT reject_test_product CHECK (\"ProductSequence\" <> 999)").ConfigureAwait(false);
        using var context = this.CreateContext();
        var buyer = await context.Context.Set<AccountEntity>().SingleAsync(account => account.Id == accounts[0]).ConfigureAwait(false);
        Assert.That(async () => await context.TrySaveCashShopPurchaseAsync(buyer.Id, accounts[1], 1, 1, () => AddPurchase(context, buyer, accounts[1], 999)).ConfigureAwait(false), Throws.InstanceOf<DbUpdateException>());
        await using (var reloaded = new AccountContext())
        {
            Assert.That(await reloaded.Set<StorageEntity>().CountAsync().ConfigureAwait(false), Is.Zero);
            Assert.That(await reloaded.Set<AccountEntity>().Select(account => account.WCoinC).ToListAsync().ConfigureAwait(false), Is.All.EqualTo(100));
        }

        buyer.WCoinC = 100;
        foreach (var entry in context.Context.ChangeTracker.Entries<StorageEntity>().ToList())
        {
            await context.DeleteAsync(entry.Entity).ConfigureAwait(false);
        }

        Assert.That(await context.SaveChangesAsync().ConfigureAwait(false), Is.True);
        Assert.That(await context.TrySaveCashShopPurchaseAsync(buyer.Id, accounts[1], 1, 1, () => AddPurchase(context, buyer, accounts[1])).ConfigureAwait(false), Is.True);
        await using var final = new AccountContext();
        Assert.That(await final.Set<StorageEntity>().CountAsync().ConfigureAwait(false), Is.EqualTo(1));
        Assert.That(await final.Set<AccountEntity>().Where(account => account.Id == buyer.Id).Select(account => account.WCoinC).SingleAsync().ConfigureAwait(false), Is.EqualTo(80));
    }

    private static void AddPurchase(IPlayerContext context, AccountEntity buyer, Guid recipient, int product = 24)
    {
        buyer.WCoinC -= 20;
        var item = context.CreateNew<DataModel.Entities.CashShopStorageItem>();
        item.AccountId = recipient;
        item.ProductSequence = product;
        item.PriceSequence = 40;
        item.AddedAt = DateTime.UtcNow;
        item.IsGift = true;
    }

    private PlayerContext CreateContext()
    {
        var provider = new PersistenceContextProvider(NullLoggerFactory.Instance, null);
        return (PlayerContext)provider.CreateNewPlayerContext(new Persistence.BasicModel.GameConfiguration());
    }

    private async ValueTask<Guid[]> CreateAccountsAsync(int count)
    {
        await using var context = new AccountContext();
        var accounts = Enumerable.Range(0, count).Select(_ => new AccountEntity { Id = Guid.NewGuid(), WCoinC = 100 }).ToArray();
        context.AddRange(accounts);
        await context.SaveChangesAsync().ConfigureAwait(false);
        return accounts.Select(account => account.Id).ToArray();
    }

    private async ValueTask<int> GetBackendIdAsync(PlayerContext context)
    {
        await context.Context.Database.OpenConnectionAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT pg_backend_pid()", (NpgsqlConnection)context.Context.Database.GetDbConnection());
        return (int)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private async ValueTask ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private sealed class CashSettings(string connection) : IDatabaseConnectionSettingProvider
    {
        public Task? Initialization => Task.CompletedTask;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ConnectionSetting GetConnectionSetting<TContextType>()
            where TContextType : DbContext => this.GetConnectionSetting(typeof(TContextType));

        public ConnectionSetting GetConnectionSetting(Type contextType) => new()
        {
            ContextTypeName = contextType.FullName,
            ConnectionString = connection,
            DatabaseEngine = DatabaseEngine.Npgsql,
        };
    }
}
