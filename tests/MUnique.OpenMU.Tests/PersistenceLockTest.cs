// <copyright file="PersistenceLockTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Threading;

/// <summary>
/// Tests for the per-player persistence lock (<see cref="MUnique.OpenMU.GameLogic.Player.RunPersistenceExclusiveAsync(System.Func{System.Threading.Tasks.ValueTask},System.Threading.CancellationToken)"/>),
/// which serializes a player's context mutations against its periodic/disconnect progress saves so they
/// can never run concurrently. Without it, a mutation running during <c>SaveChangesAsync</c> corrupts the
/// change tracker and rolls the whole session back.
/// </summary>
[TestFixture]
public class PersistenceLockTest
{
    /// <summary>
    /// Verifies that concurrent exclusive operations for the same player never overlap.
    /// </summary>
    [Test]
    public async Task ConcurrentAccessIsSerializedAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var concurrent = 0;
        var overlapDetected = false;

        async ValueTask BodyAsync()
        {
            if (Interlocked.Increment(ref concurrent) > 1)
            {
                overlapDetected = true;
            }

            await Task.Delay(1).ConfigureAwait(false);
            Interlocked.Decrement(ref concurrent);
        }

        var tasks = Enumerable.Range(0, 50)
            .Select(_ => player.RunPersistenceExclusiveAsync(BodyAsync).AsTask())
            .ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);

        Assert.That(overlapDetected, Is.False, "Two exclusive operations for the same player ran at the same time.");
    }

    /// <summary>
    /// Verifies that re-entering the lock from within an already-held exclusive scope does not deadlock
    /// (an inline save inside a packet handler is exactly this case).
    /// </summary>
    [Test]
    public async Task ReentrantAccessDoesNotDeadlockAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var executed = 0;

        var run = player.RunPersistenceExclusiveAsync(async () =>
        {
            Interlocked.Increment(ref executed);
            await player.RunPersistenceExclusiveAsync(async () =>
            {
                Interlocked.Increment(ref executed);
                await Task.Yield();
            }).ConfigureAwait(false);
        }).AsTask();

        // If reentrancy deadlocked, this would hang; fail fast instead of blocking the suite.
        await run.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.That(executed, Is.EqualTo(2));
    }

    /// <summary>
    /// Verifies that a re-entrant exclusive operation still runs while another flow holds the lock:
    /// the outer flow keeps the lock, an independent flow must wait, and the re-entrant call inside the
    /// outer flow proceeds without waiting for itself.
    /// </summary>
    [Test]
    public async Task IndependentFlowWaitsWhileLockIsHeldAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var otherEntered = false;
        var holderHasLock = new TaskCompletionSource();
        var mayRelease = new TaskCompletionSource();

        // Holder runs on its own flow and keeps the lock until signalled.
        var holder = Task.Run(() => player.RunPersistenceExclusiveAsync(async () =>
        {
            holderHasLock.SetResult();

            // A re-entrant call from the holding flow must NOT block on the lock we already hold.
            await player.RunPersistenceExclusiveAsync(() => ValueTask.CompletedTask).ConfigureAwait(false);

            await mayRelease.Task.ConfigureAwait(false);
        }).AsTask());

        await holderHasLock.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // Competing flow started from an INDEPENDENT context (does not inherit the reentrancy flag).
        var other = Task.Run(() => player.RunPersistenceExclusiveAsync(() =>
        {
            otherEntered = true;
            return ValueTask.CompletedTask;
        }).AsTask());

        await Task.Delay(50).ConfigureAwait(false);
        Assert.That(otherEntered, Is.False, "An independent flow entered while the lock was held.");

        mayRelease.SetResult();
        await holder.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await other.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.That(otherEntered, Is.True, "The competing flow never ran after the lock was released.");
    }

    /// <summary>
    /// Opposite-order paired callers serialize without a global lock or a reverse-order cycle.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task OppositeOrderPairsFinishWithoutOverlapAsync()
    {
        var first = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var second = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var active = 0;
        var overlap = false;
        async ValueTask BodyAsync()
        {
            if (Interlocked.Increment(ref active) > 1)
            {
                overlap = true;
            }

            await Task.Yield();
            Interlocked.Decrement(ref active);
        }

        var tasks = Enumerable.Range(0, 50).Select(index => Task.Run(() =>
            (index % 2 == 0 ? first.RunPersistenceExclusiveAsync(second, BodyAsync) : second.RunPersistenceExclusiveAsync(first, BodyAsync)).AsTask()));
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.That(overlap, Is.False);
    }

    /// <summary>
    /// Each player's independent operation excludes a paired operation.
    /// </summary>
    /// <param name="holdPartner">Whether the existing lock belongs to the second player.</param>
    /// <returns>The task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task PairWaitsForEitherPlayerAsync(bool holdPartner)
    {
        var first = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var second = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = holdPartner ? second : first;
        var holder = Task.Run(() => owner.RunPersistenceExclusiveAsync(async () =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
        }).AsTask());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var executed = false;
        var pair = first.RunPersistenceExclusiveAsync(second, () =>
        {
            executed = true;
            return ValueTask.CompletedTask;
        }).AsTask();
        try
        {
            Assert.That(pair.IsCompleted, Is.False);
            Assert.That(executed, Is.False);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(holder, pair).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        Assert.That(executed, Is.True);
    }

    /// <summary>
    /// A caller cannot invert the lock order by entering a pair from only one player's lock.
    /// </summary>
    /// <param name="holdPartner">Whether the caller already holds the second player's lock.</param>
    /// <returns>The task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task PairRejectsPartialReentryAsync(bool holdPartner)
    {
        var first = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var second = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var owner = holdPartner ? second : first;
        await owner.RunPersistenceExclusiveAsync(() =>
        {
            Assert.That(async () => await first.RunPersistenceExclusiveAsync(second, () => ValueTask.CompletedTask).ConfigureAwait(false),
                Throws.TypeOf<InvalidOperationException>());
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// A paired scope may reenter both players, either pair order, and a single-player self pair.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task PairSupportsCompleteReentryAsync()
    {
        var first = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var second = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var executed = 0;
        var pair = first.RunPersistenceExclusiveAsync(second, async () =>
        {
            await first.RunPersistenceExclusiveAsync(() =>
            {
                executed++;
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
            await second.RunPersistenceExclusiveAsync(first, () =>
            {
                executed++;
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
            await first.RunPersistenceExclusiveAsync(first, () =>
            {
                executed++;
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
        }).AsTask();
        await pair.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.That(executed, Is.EqualTo(3));
    }
}
