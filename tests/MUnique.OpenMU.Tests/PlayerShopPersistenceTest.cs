// <copyright file="PlayerShopPersistenceTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.CashShop;
using MUnique.OpenMU.GameLogic.PlayerActions.PlayerStore;
using MUnique.OpenMU.GameLogic.PlayerShopCurrencies;
using MUnique.OpenMU.GameLogic.Views.Inventory;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Verifies the purchase commit boundary and recovery for each supported payment strategy.
/// </summary>
[TestFixture]
public class PlayerShopPersistenceTest
{
    /// <summary>
    /// No payment or success notification precedes the commit; a rejected commit restores the exact sale.
    /// </summary>
    /// <param name="currency">The price currency.</param>
    /// <param name="outcome">The result of the persistence operation.</param>
    /// <returns>The task.</returns>
    [Test]
    [Combinatorial]
    public async Task PurchaseFollowsCommitAsync(
        [Values("zen", "jewel", "WCoinC", "WCoinP", "GoblinPoints")] string currency,
        [Values("success", "rejected", "exception", "view-failure")] string outcome)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var balances = new Dictionary<ItemStorage, int>();
        var coins = new Dictionary<(Account Account, CashShopCoinType CoinType), int>();
        var trade = new Mock<ITradeContext>();
        trade.Setup(c => c.SetMoney(It.IsAny<ItemStorage>(), It.IsAny<int>()))
            .Callback<ItemStorage, int>((storage, money) => balances[storage] = money);
        trade.Setup(c => c.SetCashShopCoins(It.IsAny<Account>(), It.IsAny<CashShopCoinType>(), It.IsAny<int>()))
            .Callback<Account, CashShopCoinType, int>((account, type, balance) => coins[(account, type)] = balance);
        trade.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(async () =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            if (outcome == "exception")
            {
                throw new InvalidOperationException("Rejected personal store transaction.");
            }

            return outcome is "success" or "view-failure";
        });
        var memory = new InMemoryPersistenceContextProvider();
        var provider = new Mock<IPersistenceContextProvider>();
        provider.Setup(p => p.CreateNewTradeContext()).Returns(trade.Object);
        provider.Setup(p => p.CreateNewPlayerContext(It.IsAny<GameConfiguration>()))
            .Returns<GameConfiguration>(memory.CreateNewPlayerContext);
        var configuration = PlayerTestHelper.CreateGameConfiguration();
        configuration.MaximumInventoryMoney = 1000;
        Mock.Get(configuration).Setup(c => c.JewelMixes).Returns(new List<JewelMix>());
        var maps = new MapInitializer(configuration, NullLogger<MapInitializer>.Instance, NullDropGenerator.Instance, null);
        await using var game = new GameContext(configuration, provider.Object, maps, NullLoggerFactory.Instance,
            new PlugInManager(null, NullLoggerFactory.Instance, null, null), NullDropGenerator.Instance, new ConfigurationChangeMediator());
        maps.PlugInManager = game.PlugInManager;
        maps.PathFinderPool = game.PathFinderPool;
        game.StopPeriodicTasks();
        await using var seller = await PlayerTestHelper.CreatePlayerAsync(game).ConfigureAwait(false);
        await using var buyer = await PlayerTestHelper.CreatePlayerAsync(game).ConfigureAwait(false);
        seller.Money = 100;
        buyer.Money = 100;
        var jewelDefinition = new Persistence.BasicModel.ItemDefinition { Group = 14, Number = 13, Width = 1, Height = 1, Durability = 1 };
        var jewel = new Persistence.BasicModel.Item { Id = Guid.NewGuid(), Definition = jewelDefinition, ItemSlot = 20, Durability = 1 };
        var coin = Enum.TryParse<CashShopCoinType>(currency, out var coinType) ? coinType : (CashShopCoinType?)null;
        var feature = new PlayerShopCurrenciesConfiguration { IsWCoinCAllowed = true, IsWCoinPAllowed = true, IsGoblinPointsAllowed = true };
        feature.PriceItems.Add(jewelDefinition);
        game.FeaturePlugIns.AddPlugIn(new PlayerShopCurrenciesFeaturePlugIn { Configuration = feature }, true);
        if (coin is { } type)
        {
            buyer.Account!.SetCashShopCoins(type, 100);
            seller.Account!.SetCashShopCoins(type, 100);
        }

        var price = coin is { } selected ? new ShopPrice(20, CoinType: selected)
            : currency == "jewel" ? new ShopPrice(1, jewelDefinition) : new ShopPrice(20);
        if (currency == "jewel")
        {
            Assert.That(await buyer.Inventory!.AddItemAsync(20, jewel).ConfigureAwait(false), Is.True);
        }

        var sold = new Persistence.BasicModel.Item
        {
            Id = Guid.NewGuid(),
            Definition = new Persistence.BasicModel.ItemDefinition { Group = 0, Number = 1, Width = 1, Height = 2, Durability = 20 },
            ItemSlot = 206,
            Durability = 20,
        };
        sold.SetStorePrice(price);
        Assert.That(await seller.ShopStorage!.AddItemAsync(206, sold).ConfigureAwait(false), Is.True);
        seller.ShopStorage.StoreOpen = true;
        var buyerView = Mock.Get(buyer.ViewPlugIns.GetPlugIn<IPlayerShopBuyRequestResultPlugIn>()!);
        var soldView = Mock.Get(seller.ViewPlugIns.GetPlugIn<IItemSoldByPlayerShopPlugIn>()!);
        buyerView.Invocations.Clear();
        soldView.Invocations.Clear();
        if (outcome == "view-failure")
        {
            soldView.Setup(v => v.ItemSoldByPlayerShopAsync(206, buyer)).Throws(new InvalidOperationException("Failed post-commit view."));
        }

        var purchase = new BuyRequestAction().BuyItemAsync(buyer, seller, 206).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var buyerSave = buyer.SaveProgressAsync().AsTask();
        var sellerSave = seller.SaveProgressAsync().AsTask();
        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(buyer.Money, Is.EqualTo(100), "Money remains unchanged until commit.");
                Assert.That(seller.Money, Is.EqualTo(100));
                if (coin is { } currentCoin)
                {
                    Assert.That(buyer.Account!.GetCashShopCoins(currentCoin), Is.EqualTo(100));
                    Assert.That(seller.Account!.GetCashShopCoins(currentCoin), Is.EqualTo(100));
                }

                Assert.That(buyerView.Invocations, Is.Empty);
                Assert.That(soldView.Invocations, Is.Empty);
                Assert.That(buyerSave.IsCompleted, Is.False, "A buyer save cannot publish an uncommitted transfer.");
                Assert.That(sellerSave.IsCompleted, Is.False, "A seller save cannot publish an uncommitted transfer.");
            });
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(purchase, buyerSave, sellerSave).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        var committed = outcome is "success" or "view-failure";
        Assert.Multiple(() =>
        {
            Assert.That(buyer.Inventory!.Items.Any(i => ReferenceEquals(i, sold)), Is.EqualTo(committed));
            Assert.That(seller.ShopStorage.GetItem(206), committed ? Is.Null : Is.SameAs(sold));
            Assert.That(sold.GetStorePrice(), committed ? Is.Null : Is.EqualTo(price));
            Assert.That(buyer.Money, Is.EqualTo(committed && currency == "zen" ? 80 : 100));
            Assert.That(seller.Money, Is.EqualTo(committed && currency == "zen" ? 120 : 100));
            if (coin is { } currentCoin)
            {
                Assert.That(buyer.Account!.GetCashShopCoins(currentCoin), Is.EqualTo(committed ? 80 : 100));
                Assert.That(seller.Account!.GetCashShopCoins(currentCoin), Is.EqualTo(committed ? 120 : 100));
            }

            if (currency == "jewel")
            {
                Assert.That(buyer.Inventory.Items.Any(i => ReferenceEquals(i, jewel)), Is.EqualTo(!committed));
                Assert.That(seller.Inventory!.Items.Any(i => ReferenceEquals(i, jewel)), Is.EqualTo(committed));
                if (!committed)
                {
                    Assert.That(buyer.Inventory.GetItem(20), Is.SameAs(jewel));
                }
            }

            buyerView.Verify(v => v.ShowResultAsync(seller, ItemBuyResult.Success, sold), committed ? Times.Once() : Times.Never());
            soldView.Verify(v => v.ItemSoldByPlayerShopAsync(206, buyer), committed ? Times.Once() : Times.Never());
        });
        if (currency == "zen" && committed)
        {
            Assert.That(balances[seller.SelectedCharacter!.Inventory!], Is.EqualTo(120));
            Assert.That(balances[buyer.SelectedCharacter!.Inventory!], Is.EqualTo(80));
        }

        if (coin is { } persistedCoin && committed)
        {
            Assert.That(coins[(buyer.Account!, persistedCoin)], Is.EqualTo(80));
            Assert.That(coins[(seller.Account!, persistedCoin)], Is.EqualTo(120));
        }

        if (!committed)
        {
            Assert.That(seller.ShopStorage.StoreOpen, Is.True);
            trade.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(true));
            await new BuyRequestAction().BuyItemAsync(buyer, seller, 206).ConfigureAwait(false);
            buyerView.Verify(v => v.ShowResultAsync(seller, ItemBuyResult.Success, sold), Times.Once);
            Assert.That(buyer.Inventory!.Items.Any(i => ReferenceEquals(i, sold)), Is.True, "The same connection can retry after a rejected save.");
        }
    }
}
