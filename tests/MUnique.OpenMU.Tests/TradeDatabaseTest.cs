// <copyright file="TradeDatabaseTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.EntityFramework;
using Npgsql;
using AccountEntity = MUnique.OpenMU.Persistence.EntityFramework.Model.Account;
using ItemEntity = MUnique.OpenMU.Persistence.EntityFramework.Model.Item;
using StorageEntity = MUnique.OpenMU.Persistence.EntityFramework.Model.ItemStorage;

/// <summary>
/// Verifies an actual trade commit and rollback on a newly created disposable PostgreSQL database.
/// </summary>
[TestFixture]
[Explicit("Requires OPENMU_TRADE_TEST_CONNECTION pointing to a disposable PostgreSQL cluster.")]
[NonParallelizable]
public class TradeDatabaseTest
{
    private string _maintenance = null!;
    private string _database = null!;
    private string _connection = null!;
    private long _oid;
    private IDisposable? _settings;

    /// <summary>
    /// Creates a separate database owned by this test invocation.
    /// </summary>
    /// <returns>The task.</returns>
    [SetUp]
    public async ValueTask SetUpAsync()
    {
        var configured = Environment.GetEnvironmentVariable("OPENMU_TRADE_TEST_CONNECTION");
        Assert.That(configured, Is.Not.Null.And.Not.Empty);
        var builder = new NpgsqlConnectionStringBuilder(configured) { Database = "postgres", Pooling = false };
        this._maintenance = builder.ConnectionString;
        this._database = "openmu_trade_test_" + Guid.NewGuid().ToString("N");
        await this.ExecuteAsync(this._maintenance, $"CREATE DATABASE \"{this._database}\" WITH TEMPLATE template0").ConfigureAwait(false);
        builder.Database = this._database;
        this._connection = builder.ConnectionString;
        await using var connection = new NpgsqlConnection(this._maintenance);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT oid::bigint FROM pg_database WHERE datname = @name", connection);
        command.Parameters.AddWithValue("name", this._database);
        this._oid = (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
        this._settings = ConnectionConfigurator.UseTemporarily(new TradeSettings(this._connection));
        await using var context = new TradeContext();
        // This test exercises transaction behavior, not schema initialization or database roles.
        // Build only the two tables it uses from the actual model's column mappings.
        await this.ExecuteAsync(this._connection, "CREATE SCHEMA data").ConfigureAwait(false);
        foreach (var type in new[] { typeof(StorageEntity), typeof(ItemEntity), typeof(AccountEntity) })
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
    /// Drops only the exact database created by this test and restores the connection settings.
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
    /// The item transfer and both balances either all commit or all remain unchanged.
    /// </summary>
    /// <param name="rejectBalance">Whether a database constraint rejects the buyer's balance.</param>
    /// <returns>The task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async ValueTask ItemAndBalancesHaveOneCommitAsync(bool rejectBalance)
    {
        var seller = new StorageEntity { Id = Guid.NewGuid(), Money = 100 };
        var buyer = new StorageEntity { Id = Guid.NewGuid(), Money = 100 };
        var item = new ItemEntity { Id = Guid.NewGuid(), ItemStorageId = seller.Id, ItemSlot = 20, DefinitionId = Guid.NewGuid() };
        await using (var initial = new TradeContext())
        {
            initial.AddRange(seller, buyer, item);
            await initial.SaveChangesAsync().ConfigureAwait(false);
        }

        if (rejectBalance)
        {
            await this.ExecuteAsync(this._connection, "ALTER TABLE data.\"ItemStorage\" ADD CONSTRAINT reject_trade_balance CHECK (\"Money\" >= 100)").ConfigureAwait(false);
        }

        var provider = new PersistenceContextProvider(NullLoggerFactory.Instance, null);
        using (var trade = provider.CreateNewTradeContext())
        {
            trade.Attach(item);
            item.ItemStorageId = buyer.Id;
            item.ItemSlot = 12;
            trade.SetMoney(seller, 160);
            trade.SetMoney(buyer, 40);
            Assert.That(seller.Money, Is.EqualTo(100), "Scheduling persistence must not mutate live balances.");
            Assert.That(buyer.Money, Is.EqualTo(100));
            if (rejectBalance)
            {
                Assert.That(async () => await trade.SaveChangesAsync().ConfigureAwait(false), Throws.InstanceOf<PersistenceConflictException>());
            }
            else
            {
                Assert.That(await trade.SaveChangesAsync().ConfigureAwait(false), Is.True);
            }
        }

        await using var reloaded = new TradeContext();
        var storedItem = await reloaded.Set<ItemEntity>().SingleAsync().ConfigureAwait(false);
        var storedSeller = await reloaded.Set<StorageEntity>().SingleAsync(s => s.Id == seller.Id).ConfigureAwait(false);
        var storedBuyer = await reloaded.Set<StorageEntity>().SingleAsync(s => s.Id == buyer.Id).ConfigureAwait(false);
        Assert.That(storedItem.Id, Is.EqualTo(item.Id));
        Assert.That(storedItem.ItemStorageId, Is.EqualTo(rejectBalance ? seller.Id : buyer.Id));
        Assert.That(storedItem.ItemSlot, Is.EqualTo(rejectBalance ? 20 : 12));
        Assert.That(storedSeller.Money, Is.EqualTo(rejectBalance ? 100 : 160));
        Assert.That(storedBuyer.Money, Is.EqualTo(rejectBalance ? 100 : 40));
        Assert.That(storedSeller.Money + storedBuyer.Money, Is.EqualTo(200));
    }

    /// <summary>
    /// Personal-store account coins and the original item share one database commit, including rejection.
    /// </summary>
    /// <param name="coinType">The account currency to transfer.</param>
    /// <param name="rejectBalance">Whether a database constraint rejects the debit.</param>
    /// <returns>The task.</returns>
    [Test]
    [Combinatorial]
    public async ValueTask ItemAndAccountCoinsHaveOneCommitAsync(
        [Values(CashShopCoinType.WCoinC, CashShopCoinType.WCoinP, CashShopCoinType.GoblinPoints)] CashShopCoinType coinType,
        [Values(false, true)] bool rejectBalance)
    {
        var seller = new AccountEntity { Id = Guid.NewGuid(), LoginName = "seller", WCoinC = 100, WCoinP = 100, GoblinPoints = 100 };
        var buyer = new AccountEntity { Id = Guid.NewGuid(), LoginName = "buyer", WCoinC = 100, WCoinP = 100, GoblinPoints = 100 };
        var sellerInventory = new StorageEntity { Id = Guid.NewGuid(), Money = 700 };
        var buyerInventory = new StorageEntity { Id = Guid.NewGuid(), Money = 900 };
        var item = new ItemEntity { Id = Guid.NewGuid(), ItemStorageId = sellerInventory.Id, ItemSlot = 204, StorePrice = 60, DefinitionId = Guid.NewGuid() };
        await using (var initial = new TradeContext())
        {
            initial.AddRange(seller, buyer, sellerInventory, buyerInventory, item);
            await initial.SaveChangesAsync().ConfigureAwait(false);
        }

        var property = coinType.ToString();
        if (rejectBalance)
        {
            await this.ExecuteAsync(this._connection, $"ALTER TABLE data.\"Account\" ADD CONSTRAINT reject_shop_debit CHECK (\"{property}\" >= 100)").ConfigureAwait(false);
        }

        var provider = new PersistenceContextProvider(NullLoggerFactory.Instance, null);
        using (var trade = provider.CreateNewTradeContext())
        {
            trade.Attach(item);
            item.ItemStorageId = buyerInventory.Id;
            item.ItemSlot = 12;
            item.StorePrice = null;
            trade.SetCashShopCoins(buyer, coinType, 40);
            trade.SetCashShopCoins(seller, coinType, 160);
            Assert.That(new[] { buyer.WCoinC, buyer.WCoinP, buyer.GoblinPoints, seller.WCoinC, seller.WCoinP, seller.GoblinPoints }, Is.All.EqualTo(100), "Staging must not alter either live account.");
            if (rejectBalance)
            {
                Assert.That(async () => await trade.SaveChangesAsync().ConfigureAwait(false), Throws.InstanceOf<PersistenceConflictException>());
            }
            else
            {
                Assert.That(await trade.SaveChangesAsync().ConfigureAwait(false), Is.True);
            }
        }

        await using var reloaded = new TradeContext();
        var storedItem = await reloaded.Set<ItemEntity>().SingleAsync().ConfigureAwait(false);
        var storedSeller = await reloaded.Set<AccountEntity>().SingleAsync(a => a.Id == seller.Id).ConfigureAwait(false);
        var storedBuyer = await reloaded.Set<AccountEntity>().SingleAsync(a => a.Id == buyer.Id).ConfigureAwait(false);
        Assert.That(storedItem.ItemStorageId, Is.EqualTo(rejectBalance ? sellerInventory.Id : buyerInventory.Id));
        Assert.That(storedItem.ItemSlot, Is.EqualTo(rejectBalance ? 204 : 12));
        Assert.That(storedItem.StorePrice, rejectBalance ? Is.EqualTo(60) : Is.Null);
        Assert.That(storedSeller.LoginName, Is.EqualTo("seller"), "Identity stubs must not overwrite unrelated account fields.");
        Assert.That(storedBuyer.LoginName, Is.EqualTo("buyer"));
        foreach (var type in Enum.GetValues<CashShopCoinType>())
        {
            var sellerBalance = GameLogic.CashShop.CashShopCoinExtensions.GetCashShopCoins(storedSeller, type);
            var buyerBalance = GameLogic.CashShop.CashShopCoinExtensions.GetCashShopCoins(storedBuyer, type);
            Assert.That(sellerBalance, Is.EqualTo(!rejectBalance && type == coinType ? 160 : 100));
            Assert.That(buyerBalance, Is.EqualTo(!rejectBalance && type == coinType ? 40 : 100));
            Assert.That(sellerBalance + buyerBalance, Is.EqualTo(200));
        }

        Assert.That(await reloaded.Set<StorageEntity>().SumAsync(s => s.Money).ConfigureAwait(false), Is.EqualTo(1600));
    }

    private async ValueTask ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private sealed class TradeSettings(string connection) : IDatabaseConnectionSettingProvider
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
