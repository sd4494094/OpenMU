// <copyright file="TradeConcurrencyTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Threading;
using Moq;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlayerActions.Trade;
using MUnique.OpenMU.GameLogic.Views.Trade;

/// <summary>
/// Tests overlapping trade confirmations and cancellation.
/// </summary>
[TestFixture]
public class TradeConcurrencyTest
{
    /// <summary>
    /// Both players can confirm together without acquiring each other's state locks in reverse order.
    /// </summary>
    /// <param name="sharedAction">Whether both requests use the same action instance.</param>
    /// <returns>The task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async ValueTask SimultaneousConfirmationsFinishOnceAsync(bool sharedAction)
    {
        var (first, second) = await CreateTradeAsync().ConfigureAwait(false);
        var pressed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pressedCount = 0;
        var leavingCount = 0;
        foreach (var actor in new[] { first, second })
        {
            actor.PlayerState.StateChanged += async args =>
            {
                if (args.CurrentStateState == PlayerState.TradeButtonPressed)
                {
                    await CoordinateAsync(pressed, Interlocked.Increment(ref pressedCount)).ConfigureAwait(false);
                }
            };
            actor.PlayerState.StateChanges += async args =>
            {
                if (args.NextState == PlayerState.EnteredWorld)
                {
                    await CoordinateAsync(leaving, Interlocked.Increment(ref leavingCount)).ConfigureAwait(false);
                }
            };
        }

        var firstAction = new TradeButtonAction();
        var secondAction = sharedAction ? firstAction : new TradeButtonAction();
        var both = Task.WhenAll(
            Task.Run(async () => await firstAction.TradeButtonChangedAsync(first, TradeButtonState.Checked).ConfigureAwait(false)),
            Task.Run(async () => await secondAction.TradeButtonChangedAsync(second, TradeButtonState.Checked).ConfigureAwait(false)));
        await both.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        AssertCompletedTrade(first, second);
    }

    /// <summary>
    /// A cancellation waiting for an already completing trade cannot report a second, contradictory result.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async ValueTask CancellationDuringCompletionDoesNotSendAnotherResultAsync()
    {
        var (first, second) = await CreateTradeAsync().ConfigureAwait(false);
        await new TradeButtonAction().TradeButtonChangedAsync(first, TradeButtonState.Checked).ConfigureAwait(false);
        var finishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        second.PlayerState.StateChanges += async args =>
        {
            if (args.NextState == PlayerState.EnteredWorld)
            {
                finishing.TrySetResult();
                await release.Task.ConfigureAwait(false);
            }
        };

        var finish = new TradeButtonAction().TradeButtonChangedAsync(second, TradeButtonState.Checked).AsTask();
        try
        {
            await finishing.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var cancel = new TradeCancelAction().CancelTradeAsync(first).AsTask();
            release.TrySetResult();
            await Task.WhenAll(finish, cancel).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            release.TrySetResult();
        }

        AssertCompletedTrade(first, second);
    }

    private static async ValueTask CoordinateAsync(TaskCompletionSource gate, int count)
    {
        if (count == 2)
        {
            gate.TrySetResult();
        }

        // Expose the opposite-lock schedule when both requests can proceed.
        // A serialized request must also be able to continue without its waiting peer.
        try
        {
            await gate.Task.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The other request may be waiting for this operation to finish.
        }
    }

    private static async ValueTask<(Player First, Player Second)> CreateTradeAsync()
    {
        var first = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var second = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        first.GameContext.Configuration.MaximumInventoryMoney = 1000;
        second.GameContext.Configuration.MaximumInventoryMoney = 1000;
        first.Money = 100;
        second.Money = 100;
        Assert.That(await new TradeRequestAction().RequestTradeAsync(first, second).ConfigureAwait(false), Is.True);
        await new TradeAcceptAction().HandleTradeAcceptAsync(second, true).ConfigureAwait(false);
        await new TradeMoneyAction().TradeMoneyAsync(first, 60).ConfigureAwait(false);
        return (first, second);
    }

    private static void AssertCompletedTrade(Player first, Player second)
    {
        Assert.That(first.Money, Is.EqualTo(40));
        Assert.That(second.Money, Is.EqualTo(160));
        foreach (var actor in new[] { first, second })
        {
            Assert.That(actor.PlayerState.CurrentState, Is.EqualTo(PlayerState.EnteredWorld));
            Assert.That(actor.TradingPartner, Is.Null);
            Assert.That(actor.BackupInventory, Is.Null);
            var finished = Mock.Get(actor.ViewPlugIns.GetPlugIn<ITradeFinishedPlugIn>()!);
            finished.Verify(view => view.TradeFinishedAsync(TradeResult.Success), Times.Once);
            finished.Verify(view => view.TradeFinishedAsync(It.IsAny<TradeResult>()), Times.Once);
        }
    }
}
