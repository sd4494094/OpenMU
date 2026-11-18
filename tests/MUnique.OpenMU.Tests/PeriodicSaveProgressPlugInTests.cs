// <copyright file="PeriodicSaveProgressPlugInTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlayerActions.Trade;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.Views.Trade;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Verifies saves with the same plug-in instance shared by multiple game contexts.
/// </summary>
[TestFixture]
public class PeriodicSaveProgressPlugInTests
{
    private readonly List<GameContext> _contexts = new();

    /// <summary>
    /// Disposes contexts after the assertions have observed actual save calls.
    /// </summary>
    [TearDown]
    public async Task TearDownAsync()
    {
        foreach (var context in this._contexts)
        {
            await context.DisposeAsync().ConfigureAwait(false);
        }

        this._contexts.Clear();
    }

    /// <summary>
    /// An empty server must not consume the interval of a server with players.
    /// </summary>
    [Test]
    public async Task EmptyContextDoesNotSuppressAnotherContextsSaveAsync()
    {
        var first = this.CreateContext(new Mock<IPlayerContext>());
        var (second, _, persistence) = await this.CreatePlayerAsync().ConfigureAwait(false);
        var plugIn = CreatePlugIn();

        await plugIn.ExecuteTaskAsync(first).ConfigureAwait(false);
        await plugIn.ExecuteTaskAsync(second).ConfigureAwait(false);

        persistence.Verify(p => p.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Each context consumes a force request independently and only once.
    /// </summary>
    [Test]
    public async Task ForceStartReachesEveryContextOnceAsync()
    {
        var (first, _, firstPersistence) = await this.CreatePlayerAsync().ConfigureAwait(false);
        var (second, _, secondPersistence) = await this.CreatePlayerAsync().ConfigureAwait(false);
        var plugIn = CreatePlugIn();
        await plugIn.ExecuteTaskAsync(first).ConfigureAwait(false);
        await plugIn.ExecuteTaskAsync(second).ConfigureAwait(false);
        plugIn.ForceStart();
        await plugIn.ExecuteTaskAsync(first).ConfigureAwait(false);
        await plugIn.ExecuteTaskAsync(second).ConfigureAwait(false);
        await plugIn.ExecuteTaskAsync(first).ConfigureAwait(false);
        await plugIn.ExecuteTaskAsync(second).ConfigureAwait(false);

        firstPersistence.Verify(p => p.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        secondPersistence.Verify(p => p.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    /// <summary>
    /// Regular checks keep the configured interval without repeatedly saving.
    /// </summary>
    [Test]
    public async Task RepeatedCheckBeforeIntervalDoesNotSaveAgainAsync()
    {
        var (context, _, persistence) = await this.CreatePlayerAsync().ConfigureAwait(false);
        var plugIn = CreatePlugIn();
        await plugIn.ExecuteTaskAsync(context).ConfigureAwait(false);
        await plugIn.ExecuteTaskAsync(context).ConfigureAwait(false);

        persistence.Verify(p => p.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The existing rule still skips NPC dialogs and saves after returning to the world.
    /// </summary>
    [Test]
    public async Task NpcDialogIsSkippedUntilPlayerReturnsToWorldAsync()
    {
        var (context, player, persistence) = await this.CreatePlayerAsync().ConfigureAwait(false);
        var plugIn = CreatePlugIn();
        Assert.That(await player.PlayerState.TryAdvanceToAsync(PlayerState.NpcDialogOpened).ConfigureAwait(false), Is.True);
        await plugIn.ExecuteTaskAsync(context).ConfigureAwait(false);
        persistence.Verify(p => p.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        Assert.That(await player.PlayerState.TryAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false), Is.True);
        plugIn.ForceStart();
        await plugIn.ExecuteTaskAsync(context).ConfigureAwait(false);

        persistence.Verify(p => p.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A force during a slow save stays pending, while concurrent checks return immediately.
    /// </summary>
    [Test]
    public async Task ForceDuringSaveDoesNotOverlapOrGetLostAsync()
    {
        var (context, _, persistence) = await this.CreatePlayerAsync().ConfigureAwait(false);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        persistence.Setup(p => p.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(release.Task));
        var plugIn = CreatePlugIn();
        var first = plugIn.ExecuteTaskAsync(context).AsTask();
        Task? concurrent = null;
        try
        {
            persistence.Verify(p => p.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
            plugIn.ForceStart();
            concurrent = plugIn.ExecuteTaskAsync(context).AsTask();
            Assert.That(concurrent.IsCompletedSuccessfully, Is.True, "A duplicate tick must not queue another save.");
        }
        finally
        {
            release.TrySetResult(true);
            await first.ConfigureAwait(false);
            if (concurrent is not null)
            {
                await concurrent.ConfigureAwait(false);
            }
        }

        await plugIn.ExecuteTaskAsync(context).ConfigureAwait(false);
        await plugIn.ExecuteTaskAsync(context).ConfigureAwait(false);
        persistence.Verify(p => p.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    /// <summary>
    /// A failed save does not leave the context permanently marked as running.
    /// </summary>
    [Test]
    public async Task FailedSaveCanRunOnTheNextIntervalAsync()
    {
        var (context, _, persistence) = await this.CreatePlayerAsync().ConfigureAwait(false);
        persistence.SetupSequence(p => p.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("Expected persistence failure"))
            .Returns(new ValueTask<bool>(true));
        var plugIn = CreatePlugIn();
        plugIn.Configuration!.Interval = TimeSpan.Zero;
        await plugIn.ExecuteTaskAsync(context).ConfigureAwait(false);
        await plugIn.ExecuteTaskAsync(context).ConfigureAwait(false);

        persistence.Verify(p => p.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    /// <summary>
    /// A tick queued behind a packet must recheck its state after acquiring the persistence lock.
    /// In particular, reserved trade money must not become a durable inventory balance.
    /// </summary>
    /// <param name="nextState">The state entered by the packet which already holds the lock.</param>
    /// <returns>The task.</returns>
    [TestCase("trade")]
    [TestCase("confirmed trade")]
    [TestCase("npc")]
    [TestCase("disconnected")]
    public async Task StateChangedWhileWaitingForPersistenceLockIsRecheckedAsync(string nextState)
    {
        var (context, player, persistence) = await this.CreatePlayerAsync().ConfigureAwait(false);
        var partner = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        player.GameContext.Configuration.MaximumInventoryMoney = 1000;
        player.Money = 100;
        var savedBalances = new List<int>();
        persistence.Setup(p => p.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(() =>
        {
            savedBalances.Add(player.Money);
            return new ValueTask<bool>(true);
        });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changeState = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var packet = Task.Run(() => player.RunPersistenceExclusiveAsync(partner, async () =>
        {
            entered.TrySetResult();
            await changeState.Task.ConfigureAwait(false);
            if (nextState is "trade" or "confirmed trade")
            {
                Assert.That(await new TradeRequestAction().RequestTradeAsync(partner, player).ConfigureAwait(false), Is.True);
                await new TradeAcceptAction().HandleTradeAcceptAsync(player, true).ConfigureAwait(false);
                await new TradeMoneyAction().TradeMoneyAsync(player, 60).ConfigureAwait(false);
                if (nextState == "confirmed trade")
                {
                    await new TradeButtonAction().TradeButtonChangedAsync(player, TradeButtonState.Checked).ConfigureAwait(false);
                }

                Assert.That(player.Money, Is.EqualTo(40));
            }
            else
            {
                var state = nextState == "npc" ? PlayerState.NpcDialogOpened : PlayerState.Disconnected;
                Assert.That(await player.PlayerState.TryAdvanceToAsync(state).ConfigureAwait(false), Is.True);
            }

            // Opening the trade intentionally saves its baseline. Observe only the queued tick below.
            savedBalances.Clear();
            persistence.Invocations.Clear();
        }).AsTask());

        var plugIn = CreatePlugIn();
        Task? tick = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            tick = plugIn.ExecuteTaskAsync(context).AsTask();
            Assert.That(tick.IsCompleted, Is.False, "The tick must be waiting for the packet's persistence lock.");
        }
        finally
        {
            changeState.TrySetResult();
            await packet.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (tick is not null)
            {
                await tick.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }

        Assert.That(savedBalances, Is.Empty, "A stale EnteredWorld check must not save a later, ineligible state.");
        persistence.Verify(p => p.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        if (nextState is "trade" or "confirmed trade")
        {
            await new TradeCancelAction().CancelTradeAsync(player).ConfigureAwait(false);
            Assert.That(player.Money, Is.EqualTo(100));
            plugIn.ForceStart();
            await plugIn.ExecuteTaskAsync(context).ConfigureAwait(false);
            Assert.That(savedBalances, Is.EqualTo(new[] { 100 }), "The next eligible tick must save the restored balance.");
        }
    }

    private static PeriodicSaveProgressPlugIn CreatePlugIn()
        => new() { Configuration = new PeriodicSaveProgressPlugInConfiguration { Interval = TimeSpan.FromHours(1) } };

    private GameContext CreateContext(Mock<IPlayerContext> persistence)
    {
        var configuration = PlayerTestHelper.CreateGameConfiguration();
        var provider = new Mock<IPersistenceContextProvider>();
        provider.Setup(p => p.CreateNewPlayerContext(It.IsAny<GameConfiguration>())).Returns(persistence.Object);
        var initializer = new MapInitializer(configuration, NullLogger<MapInitializer>.Instance, NullDropGenerator.Instance, null);
        var manager = new PlugInManager(null, NullLoggerFactory.Instance, null, null);
        var context = new GameContext(configuration, provider.Object, initializer, NullLoggerFactory.Instance, manager,
            NullDropGenerator.Instance, new ConfigurationChangeMediator());
        context.StopPeriodicTasks();
        initializer.PlugInManager = manager;
        initializer.PathFinderPool = context.PathFinderPool;
        this._contexts.Add(context);
        return context;
    }

    private async ValueTask<(GameContext Context, Player Player, Mock<IPlayerContext> Persistence)> CreatePlayerAsync()
    {
        var persistence = new Mock<IPlayerContext>();
        persistence.Setup(p => p.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(true));
        var context = this.CreateContext(persistence);
        var player = await PlayerTestHelper.CreatePlayerAsync(context).ConfigureAwait(false);
        await context.AddPlayerAsync(player).ConfigureAwait(false);
        Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.EnteredWorld));
        persistence.Invocations.Clear();
        return (context, player, persistence);
    }
}
