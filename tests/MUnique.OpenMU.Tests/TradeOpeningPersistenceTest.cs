// <copyright file="TradeOpeningPersistenceTest.cs" company="MUnique">
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
using MUnique.OpenMU.GameLogic.PlayerActions.Trade;
using MUnique.OpenMU.GameLogic.Views.Trade;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Requires both progress saves before acknowledging a new trade.
/// </summary>
[TestFixture]
public class TradeOpeningPersistenceTest
{
    /// <summary>
    /// Both traders keep earlier temporary items when either inventory cannot return them.
    /// </summary>
    /// <param name="fullIndex">The full trader, with the accepting trader first.</param>
    /// <param name="freeSlots">The remaining single-cell slots, fewer than the temporary items.</param>
    /// <returns>The task.</returns>
    [TestCase(0, 0)]
    [TestCase(0, 1)]
    [TestCase(1, 0)]
    [TestCase(1, 1)]
    public async ValueTask InsufficientRoomForEarlierTemporaryItemsRejectsBothAsync(int fullIndex, int freeSlots)
    {
        var players = await CreatePlayersAsync().ConfigureAwait(false);
        var full = players[fullIndex];
        var other = players[1 - fullIndex];
        var slots = full.Inventory!.FreeSlots.ToArray();
        foreach (var slot in slots.Take(slots.Length - freeSlots))
        {
            Assert.That(await full.Inventory.AddItemAsync(slot, CreateItem()).ConfigureAwait(false), Is.True);
        }

        var initial = players.Select(p => p.Inventory!.Items.Select(item => (Item: item, Slot: item.ItemSlot)).ToArray()).ToArray();
        var balances = players.Select(p => p.Money).ToArray();
        var temporary = new[] { CreateItem(), CreateItem() };
        var otherTemporary = CreateItem();
        Assert.That(await full.TemporaryStorage!.AddItemAsync(0, temporary[0]).ConfigureAwait(false), Is.True);
        Assert.That(await full.TemporaryStorage.AddItemAsync(1, temporary[1]).ConfigureAwait(false), Is.True);
        Assert.That(await other.TemporaryStorage!.AddItemAsync(0, otherTemporary).ConfigureAwait(false), Is.True);

        Assert.That(await new TradeRequestAction().RequestTradeAsync(players[1], players[0]).ConfigureAwait(false), Is.True);
        await new TradeAcceptAction().HandleTradeAcceptAsync(players[0], true).ConfigureAwait(false);

        for (var index = 0; index < players.Length; index++)
        {
            var player = players[index];
            Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.EnteredWorld));
            Assert.That(player.TradingPartner, Is.Null);
            Assert.That(player.BackupInventory, Is.Null);
            Assert.That(player.Inventory!.Items, Is.EquivalentTo(initial[index].Select(x => x.Item)));
            Assert.That(initial[index].All(x => x.Item.ItemSlot == x.Slot), Is.True);
            Mock.Get(player.ViewPlugIns.GetPlugIn<IShowTradeRequestAnswerPlugIn>()!)
                .Verify(p => p.ShowTradeRequestAnswerAsync(true), Times.Never);
            Mock.Get(player.ViewPlugIns.GetPlugIn<IShowTradeRequestAnswerPlugIn>()!)
                .Verify(p => p.ShowTradeRequestAnswerAsync(false), Times.Once);
        }

        Assert.That(full.TemporaryStorage.Items, Is.EquivalentTo(temporary));
        Assert.That(temporary.Select(i => i.ItemSlot), Is.EqualTo(new byte[] { 0, 1 }));
        Assert.That(other.TemporaryStorage.Items, Is.EquivalentTo(new[] { otherTemporary }));
        Assert.That(otherTemporary.ItemSlot, Is.Zero);
        Assert.That(players.Select(p => p.Money), Is.EqualTo(balances));

        // Free exactly enough space, then exercise a fresh open and cancellation on the same players.
        var removed = full.Inventory.Items.Take(temporary.Length - freeSlots).ToArray();
        foreach (var item in removed)
        {
            await full.Inventory.RemoveItemAsync(item).ConfigureAwait(false);
        }

        Assert.That(await new TradeRequestAction().RequestTradeAsync(players[1], players[0]).ConfigureAwait(false), Is.True);
        await new TradeAcceptAction().HandleTradeAcceptAsync(players[0], true).ConfigureAwait(false);
        foreach (var player in players)
        {
            Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.TradeOpened));
            Assert.That(player.TemporaryStorage!.Items, Is.Empty);
            Mock.Get(player.ViewPlugIns.GetPlugIn<IShowTradeRequestAnswerPlugIn>()!)
                .Verify(p => p.ShowTradeRequestAnswerAsync(true), Times.Once);
        }

        await new TradeCancelAction().CancelTradeAsync(players[0]).ConfigureAwait(false);
        Assert.That(full.Inventory.Items, Is.EquivalentTo(initial[fullIndex].Select(x => x.Item).Except(removed).Concat(temporary)));
        Assert.That(other.Inventory!.Items, Is.EquivalentTo(new[] { otherTemporary }));
        Assert.That(players.Select(p => p.Money), Is.EqualTo(balances));
    }

    /// <summary>
    /// A failed opening save rejects both players without clearing existing temporary items.
    /// </summary>
    /// <param name="failedIndex">The failing player, with the accepting player first.</param>
    /// <param name="throws">Whether the save throws instead of returning false.</param>
    /// <returns>The task.</returns>
    [TestCase(0, false)]
    [TestCase(1, false)]
    [TestCase(0, true)]
    [TestCase(1, true)]
    public async ValueTask FailedSaveRejectsOpeningWithoutLosingItemsAsync(int failedIndex, bool throws)
    {
        var players = await CreatePlayersAsync().ConfigureAwait(false);
        var accepter = players[0];
        var requester = players[1];
        var stored = CreateItem();
        var temporary = CreateItem();
        await requester.Inventory!.AddItemAsync(20, stored).ConfigureAwait(false);
        await requester.TemporaryStorage!.AddItemAsync(0, temporary).ConfigureAwait(false);
        var balances = players.Select(p => p.Money).ToArray();
        Mock.Get(players[failedIndex].PersistenceContext)
            .Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(() => throws ? ValueTask.FromException<bool>(new InvalidOperationException("Rejected opening save.")) : new ValueTask<bool>(false));

        Assert.That(await new TradeRequestAction().RequestTradeAsync(requester, accepter).ConfigureAwait(false), Is.True);
        await new TradeAcceptAction().HandleTradeAcceptAsync(accepter, true).ConfigureAwait(false);

        foreach (var player in players)
        {
            Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.EnteredWorld));
            Assert.That(player.TradingPartner, Is.Null);
            Assert.That(player.BackupInventory, Is.Null);
            Mock.Get(player.ViewPlugIns.GetPlugIn<IShowTradeRequestAnswerPlugIn>()!)
                .Verify(p => p.ShowTradeRequestAnswerAsync(true), Times.Never);
            Mock.Get(player.ViewPlugIns.GetPlugIn<IShowTradeRequestAnswerPlugIn>()!)
                .Verify(p => p.ShowTradeRequestAnswerAsync(false), Times.Once);
        }

        Assert.That(players.Select(p => p.Money), Is.EqualTo(balances));
        Assert.That(requester.Inventory.Items, Does.Contain(stored));
        Assert.That(stored.ItemSlot, Is.EqualTo(20));
        Assert.That(requester.TemporaryStorage.Items, Does.Contain(temporary));
        Assert.That(temporary.ItemSlot, Is.Zero);

        Mock.Get(players[failedIndex].PersistenceContext)
            .Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(true));
        Assert.That(await new TradeRequestAction().RequestTradeAsync(requester, accepter).ConfigureAwait(false), Is.True);
        await new TradeAcceptAction().HandleTradeAcceptAsync(accepter, true).ConfigureAwait(false);
        foreach (var player in players)
        {
            Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.TradeOpened));
            Mock.Get(player.ViewPlugIns.GetPlugIn<IShowTradeRequestAnswerPlugIn>()!)
                .Verify(p => p.ShowTradeRequestAnswerAsync(true), Times.Once);
        }

        Assert.That(requester.Inventory.Items, Does.Contain(temporary));
        Assert.That(requester.TemporaryStorage.Items, Is.Empty);
        await new TradeCancelAction().CancelTradeAsync(accepter).ConfigureAwait(false);
        Assert.That(requester.Inventory.Items, Is.EquivalentTo(new[] { stored, temporary }));
        Assert.That(players.Select(p => p.Money), Is.EqualTo(balances));
    }

    /// <summary>
    /// The first player's success answer waits for the second player's pending save, too.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async ValueTask BothAnswersWaitForBothSavesAsync()
    {
        var players = await CreatePlayersAsync().ConfigureAwait(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock.Get(players[1].PersistenceContext).Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(async () =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return true;
        });
        Assert.That(await new TradeRequestAction().RequestTradeAsync(players[1], players[0]).ConfigureAwait(false), Is.True);
        var open = new TradeAcceptAction().HandleTradeAcceptAsync(players[0], true).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            foreach (var player in players)
            {
                Assert.That(player.BackupInventory, Is.Null);
                Mock.Get(player.ViewPlugIns.GetPlugIn<IShowTradeRequestAnswerPlugIn>()!)
                    .Verify(p => p.ShowTradeRequestAnswerAsync(true), Times.Never);
            }
        }
        finally
        {
            release.TrySetResult();
            await open.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        foreach (var player in players)
        {
            Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.TradeOpened));
            Mock.Get(player.ViewPlugIns.GetPlugIn<IShowTradeRequestAnswerPlugIn>()!)
                .Verify(p => p.ShowTradeRequestAnswerAsync(true), Times.Once);
        }
    }

    private static Item CreateItem()
    {
        var item = new Mock<Item>();
        item.SetupAllProperties();
        item.Setup(i => i.ItemOptions).Returns(new List<ItemOptionLink>());
        item.Object.Definition = new ItemDefinition { Width = 1, Height = 1, IsTradable = true };
        return item.Object;
    }

    private static async ValueTask<Player[]> CreatePlayersAsync()
    {
        var configuration = PlayerTestHelper.CreateGameConfiguration();
        var provider = new Mock<IPersistenceContextProvider>();
        provider.Setup(p => p.CreateNewPlayerContext(It.IsAny<GameConfiguration>())).Returns(() =>
        {
            var context = new Mock<IPlayerContext>();
            context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(true));
            return context.Object;
        });
        var maps = new MapInitializer(configuration, NullLogger<MapInitializer>.Instance, NullDropGenerator.Instance, null);
        var game = new GameContext(
            configuration,
            provider.Object,
            maps,
            NullLoggerFactory.Instance,
            new PlugInManager(null, NullLoggerFactory.Instance, null, null),
            NullDropGenerator.Instance,
            new ConfigurationChangeMediator());
        maps.PlugInManager = game.PlugInManager;
        maps.PathFinderPool = game.PathFinderPool;
        var first = await PlayerTestHelper.CreatePlayerAsync(game).ConfigureAwait(false);
        var second = await PlayerTestHelper.CreatePlayerAsync(game).ConfigureAwait(false);
        first.Money = 100;
        second.Money = 200;
        return [first, second];
    }
}
