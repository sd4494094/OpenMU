// <copyright file="TradeMoneyLimitTest.cs" company="MUnique">
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
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.GameLogic.PlayerActions.Trade;
using MUnique.OpenMU.GameLogic.Views.Trade;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Verifies both final balances before a trade can persist money or transfer items.
/// </summary>
[TestFixture]
public class TradeMoneyLimitTest
{
    /// <summary>
    /// Applies each recipient's configured limit to the balance after both offers.
    /// Rejection restores the original inventories without opening a trade context.
    /// </summary>
    /// <param name="firstLimit">The first recipient's money limit.</param>
    /// <param name="secondLimit">The second recipient's money limit.</param>
    /// <param name="firstBalance">The first original balance.</param>
    /// <param name="secondBalance">The second original balance.</param>
    /// <param name="firstOffer">The first offer.</param>
    /// <param name="secondOffer">The second offer.</param>
    /// <param name="succeeds">Whether the trade should succeed.</param>
    /// <param name="reverseConfirmation">Whether the second trader confirms first.</param>
    /// <returns>The task.</returns>
    [TestCaseSource(nameof(MoneyCases))]
    public async ValueTask FinalBalancesRespectBothLimitsAsync(int firstLimit, int secondLimit, int firstBalance, int secondBalance, int firstOffer, int secondOffer, bool succeeds, bool reverseConfirmation)
    {
        var saved = new Dictionary<ItemStorage, int>();
        var transaction = new Mock<ITradeContext>();
        transaction.Setup(c => c.SetMoney(It.IsAny<ItemStorage>(), It.IsAny<int>()))
            .Callback<ItemStorage, int>((storage, money) => saved.Add(storage, money));
        transaction.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var memory = new InMemoryPersistenceContextProvider();
        var provider = new Mock<IPersistenceContextProvider>();
        provider.Setup(p => p.CreateNewPlayerContext(It.IsAny<GameConfiguration>()))
            .Returns<GameConfiguration>(memory.CreateNewPlayerContext);
        provider.Setup(p => p.CreateNewTradeContext()).Returns(transaction.Object);
        var first = await CreatePlayerAsync(provider.Object, firstLimit).ConfigureAwait(false);
        var second = await CreatePlayerAsync(provider.Object, secondLimit).ConfigureAwait(false);
        first.Money = firstBalance;
        second.Money = secondBalance;
        var firstItem = CreateItem();
        var secondItem = CreateItem();
        Assert.That(await first.Inventory!.AddItemAsync(20, firstItem).ConfigureAwait(false), Is.True);
        Assert.That(await second.Inventory!.AddItemAsync(21, secondItem).ConfigureAwait(false), Is.True);
        Assert.That(await new TradeRequestAction().RequestTradeAsync(first, second).ConfigureAwait(false), Is.True);
        await new TradeAcceptAction().HandleTradeAcceptAsync(second, true).ConfigureAwait(false);
        await new MoveItemAction().MoveItemAsync(first, 20, Storages.Inventory, 0, Storages.Trade).ConfigureAwait(false);
        await new MoveItemAction().MoveItemAsync(second, 21, Storages.Inventory, 0, Storages.Trade).ConfigureAwait(false);
        await new TradeMoneyAction().TradeMoneyAsync(first, (uint)firstOffer).ConfigureAwait(false);
        await new TradeMoneyAction().TradeMoneyAsync(second, (uint)secondOffer).ConfigureAwait(false);
        Assert.That(first.TradingMoney, Is.EqualTo(firstOffer));
        Assert.That(second.TradingMoney, Is.EqualTo(secondOffer));
        Assert.That(first.TemporaryStorage!.Items, Does.Contain(firstItem));
        Assert.That(second.TemporaryStorage!.Items, Does.Contain(secondItem));

        var button = new TradeButtonAction();
        await button.TradeButtonChangedAsync(reverseConfirmation ? second : first, TradeButtonState.Checked).ConfigureAwait(false);
        await button.TradeButtonChangedAsync(reverseConfirmation ? first : second, TradeButtonState.Checked).ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(first.Money, Is.EqualTo(succeeds ? (long)firstBalance - firstOffer + secondOffer : firstBalance));
            Assert.That(second.Money, Is.EqualTo(succeeds ? (long)secondBalance - secondOffer + firstOffer : secondBalance));
            Assert.That((long)first.Money + second.Money, Is.EqualTo((long)firstBalance + secondBalance));
            Assert.That(first.Inventory.Items, Is.EquivalentTo(new[] { succeeds ? secondItem : firstItem }));
            Assert.That(second.Inventory.Items, Is.EquivalentTo(new[] { succeeds ? firstItem : secondItem }));
            if (!succeeds)
            {
                Assert.That(firstItem.ItemSlot, Is.EqualTo(20));
                Assert.That(secondItem.ItemSlot, Is.EqualTo(21));
                Assert.That(saved, Is.Empty);
            }

            foreach (var player in new[] { first, second })
            {
                Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.EnteredWorld));
                Assert.That(player.TradingPartner, Is.Null);
                Assert.That(player.BackupInventory, Is.Null);
                Assert.That(player.TemporaryStorage!.Items, Is.Empty);
                var result = succeeds ? TradeResult.Success : TradeResult.Cancelled;
                Mock.Get(player.ViewPlugIns.GetPlugIn<ITradeFinishedPlugIn>()!)
                    .Verify(p => p.TradeFinishedAsync(result), Times.Once);
                Mock.Get(player.ViewPlugIns.GetPlugIn<ITradeFinishedPlugIn>()!)
                    .Verify(p => p.TradeFinishedAsync(It.IsAny<TradeResult>()), Times.Once);
            }
        });

        provider.Verify(p => p.CreateNewTradeContext(), succeeds ? Times.Once : Times.Never);
        transaction.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), succeeds ? Times.Once : Times.Never);
        if (succeeds)
        {
            Assert.That(saved.Count, Is.EqualTo(2));
            Assert.That(saved[first.SelectedCharacter!.Inventory!], Is.EqualTo(first.Money));
            Assert.That(saved[second.SelectedCharacter!.Inventory!], Is.EqualTo(second.Money));
        }
    }

    private static IEnumerable<TestCaseData> MoneyCases()
    {
        (int FirstLimit, int SecondLimit, int FirstBalance, int SecondBalance, int FirstOffer, int SecondOffer, bool Succeeds)[] cases =
        [
            (100, 100, 100, 100, 0, 1, false),
            (100, 100, 90, 100, 0, 10, true),
            (100, 100, 90, 100, 0, 11, false),
            (100, 100, 100, 100, 1, 0, false),
            (100, 100, 100, 90, 10, 0, true),
            (100, 100, 100, 90, 11, 0, false),
            (100, 100, 100, 100, 10, 10, true),
            (100, 100, 100, 100, 0, 0, true),
            (0, 0, 0, 0, 0, 0, true),
            (100, 200, 90, 200, 0, 10, true),
            (100, 200, 100, 190, 10, 20, false),
            (200, 100, 190, 100, 20, 10, false),
            (int.MaxValue, int.MaxValue, int.MaxValue - 1, 10, 0, 1, true),
            (int.MaxValue, int.MaxValue, int.MaxValue - 1, 10, 0, 2, false),
            (int.MaxValue, int.MaxValue, 10, int.MaxValue - 1, 1, 0, true),
            (int.MaxValue, int.MaxValue, 10, int.MaxValue - 1, 2, 0, false),
        ];
        foreach (var example in cases)
        {
            foreach (var reverse in new[] { false, true })
            {
                yield return new TestCaseData(example.FirstLimit, example.SecondLimit, example.FirstBalance, example.SecondBalance, example.FirstOffer, example.SecondOffer, example.Succeeds, reverse);
            }
        }
    }

    private static async ValueTask<Player> CreatePlayerAsync(IPersistenceContextProvider provider, int maximumMoney)
    {
        var configuration = PlayerTestHelper.CreateGameConfiguration();
        configuration.MaximumInventoryMoney = maximumMoney;
        var maps = new MapInitializer(configuration, NullLogger<MapInitializer>.Instance, NullDropGenerator.Instance, null);
        var game = new GameContext(configuration, provider, maps, NullLoggerFactory.Instance,
            new PlugInManager(null, NullLoggerFactory.Instance, null, null), NullDropGenerator.Instance, new ConfigurationChangeMediator());
        maps.PlugInManager = game.PlugInManager;
        maps.PathFinderPool = game.PathFinderPool;
        return await PlayerTestHelper.CreatePlayerAsync(game).ConfigureAwait(false);
    }

    private static Item CreateItem()
    {
        var item = new Mock<Item>();
        item.SetupAllProperties();
        item.Setup(i => i.ItemOptions).Returns(new List<ItemOptionLink>());
        item.Object.Definition = new ItemDefinition { Width = 1, Height = 1, IsTradable = true };
        item.Object.Durability = 123;
        return item.Object;
    }
}
