// <copyright file="TradePersistenceTest.cs" company="MUnique">
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
/// Verifies the trade commit boundary and recovery from rejected persistence.
/// </summary>
[TestFixture]
public class TradePersistenceTest
{
    /// <summary>
    /// An unidentified inventory must not create an unrelated balance row.
    /// </summary>
    [Test]
    public void UnidentifiedInventoryCannotBePersisted()
    {
        var provider = new Persistence.EntityFramework.PersistenceContextProvider(NullLoggerFactory.Instance, null);
        using var trade = provider.CreateNewTradeContext();
        Assert.Throws<ArgumentException>(() => trade.SetMoney(new Mock<ItemStorage>().Object, 100));
    }

    /// <summary>
    /// Acknowledges a trade only after both balances have been committed with its items.
    /// A rejected save restores the original money and item instead.
    /// </summary>
    /// <param name="outcome">Whether the save succeeds, returns false, or throws.</param>
    /// <returns>The task.</returns>
    [TestCase("success")]
    [TestCase("rejected")]
    [TestCase("exception")]
    public async ValueTask TradeResultFollowsTheCommitAsync(string outcome)
    {
        var pending = new Dictionary<ItemStorage, int>();
        var saved = new Dictionary<ItemStorage, int>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new Mock<ITradeContext>();
        context.Setup(c => c.SetMoney(It.IsAny<ItemStorage>(), It.IsAny<int>()))
            .Callback<ItemStorage, int>((storage, money) => pending[storage] = money);
        context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(async () =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            if (outcome == "exception")
            {
                throw new InvalidOperationException("Rejected test transaction.");
            }

            if (outcome == "rejected")
            {
                return false;
            }

            foreach (var (storage, money) in pending)
            {
                saved[storage] = money;
            }

            return true;
        });

        var memory = new InMemoryPersistenceContextProvider();
        var provider = new Mock<IPersistenceContextProvider>();
        provider.Setup(p => p.CreateNewTradeContext()).Returns(context.Object);
        provider.Setup(p => p.CreateNewPlayerContext(It.IsAny<GameConfiguration>()))
            .Returns<GameConfiguration>(memory.CreateNewPlayerContext);
        var configuration = PlayerTestHelper.CreateGameConfiguration();
        configuration.MaximumInventoryMoney = 1000;
        var maps = new MapInitializer(configuration, NullLogger<MapInitializer>.Instance, NullDropGenerator.Instance, null);
        var game = new GameContext(configuration, provider.Object, maps, NullLoggerFactory.Instance,
            new PlugInManager(null, NullLoggerFactory.Instance, null, null), NullDropGenerator.Instance, new ConfigurationChangeMediator());
        maps.PlugInManager = game.PlugInManager;
        maps.PathFinderPool = game.PathFinderPool;
        var first = await PlayerTestHelper.CreatePlayerAsync(game).ConfigureAwait(false);
        var second = await PlayerTestHelper.CreatePlayerAsync(game).ConfigureAwait(false);
        first.Money = 100;
        second.Money = 100;
        var item = new Mock<Item>();
        item.SetupAllProperties();
        item.Setup(i => i.ItemOptions).Returns(new List<ItemOptionLink>());
        item.Object.Definition = new ItemDefinition { Width = 1, Height = 1, IsTradable = true };
        await first.Inventory!.AddItemAsync(20, item.Object).ConfigureAwait(false);
        Assert.That(await new TradeRequestAction().RequestTradeAsync(first, second).ConfigureAwait(false), Is.True);
        await new TradeAcceptAction().HandleTradeAcceptAsync(second, true).ConfigureAwait(false);
        await new MoveItemAction().MoveItemAsync(first, 20, Storages.Inventory, 0, Storages.Trade).ConfigureAwait(false);
        await new TradeMoneyAction().TradeMoneyAsync(second, 60).ConfigureAwait(false);
        await new TradeButtonAction().TradeButtonChangedAsync(first, TradeButtonState.Checked).ConfigureAwait(false);
        var finish = new TradeButtonAction().TradeButtonChangedAsync(second, TradeButtonState.Checked).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.That(first.Money, Is.EqualTo(100));
            Assert.That(second.Money, Is.EqualTo(40));
            Assert.That(pending.Keys, Is.EquivalentTo(new[] { first.SelectedCharacter!.Inventory, second.SelectedCharacter!.Inventory }),
                "Persist actual inventories, not their slot-filtering adapters.");
            foreach (var player in new[] { first, second })
            {
                Mock.Get(player.ViewPlugIns.GetPlugIn<ITradeFinishedPlugIn>()!)
                    .Verify(p => p.TradeFinishedAsync(It.IsAny<TradeResult>()), Times.Never);
            }
        }
        finally
        {
            release.TrySetResult();
            await finish.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        var succeeded = outcome == "success";
        Assert.That(first.Money, Is.EqualTo(succeeded ? 160 : 100));
        Assert.That(second.Money, Is.EqualTo(succeeded ? 40 : 100));
        Assert.That(first.Inventory.Items.Contains(item.Object), Is.EqualTo(!succeeded));
        Assert.That(second.Inventory!.Items.Contains(item.Object), Is.EqualTo(succeeded));
        if (succeeded)
        {
            Assert.That(saved[first.SelectedCharacter!.Inventory!], Is.EqualTo(160));
            Assert.That(saved[second.SelectedCharacter!.Inventory!], Is.EqualTo(40));
        }
        else
        {
            Assert.That(saved, Is.Empty);
        }

        foreach (var player in new[] { first, second })
        {
            var result = succeeded ? TradeResult.Success : TradeResult.Cancelled;
            Assert.That(player.TradingPartner, Is.Null);
            Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.EnteredWorld));
            Mock.Get(player.ViewPlugIns.GetPlugIn<ITradeFinishedPlugIn>()!)
                .Verify(p => p.TradeFinishedAsync(result), Times.Once);
        }
    }
}
