// <copyright file="ItemPickupPersistenceTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.PlugIns;
using B = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Verifies that a persistent drop is consumed only with a confirmed inventory outcome.
/// </summary>
[TestFixture]
public class ItemPickupPersistenceTests
{
    /// <summary>
    /// The ground object and the result remain pending while the inventory commit is blocked.
    /// Stacking deletes the original item in that same context before saving.
    /// </summary>
    /// <param name="stack">Whether the item merges with an existing stack.</param>
    /// <returns>The task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task PickupWaitsForCommitAsync(bool stack)
    {
        var (player, context, drop, target) = await CreateAsync(stack).ConfigureAwait(false);
        await using var ownedPlayer = player;
        await using var ownedDrop = drop;
        var map = drop.CurrentMap;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumed = false;
        context.Setup(c => c.DeleteAsync(drop.Item)).Returns(() =>
        {
            consumed = true;
            return new ValueTask<bool>(true);
        });
        context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(async () =>
        {
            Assert.That(stack ? consumed : player.Inventory!.Items.Contains(drop.Item), Is.True);
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return true;
        });

        var picking = drop.TryPickUpByAsync(player).AsTask();
        try
        {
            await Task.WhenAny(picking, entered.Task).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.That(entered.Task.IsCompleted, Is.True, "Pickup bypassed the inventory commit.");
            Assert.That(picking.IsCompleted, Is.False);
            Assert.That(map.GetObject(drop.Id), Is.SameAs(drop), "A pending commit must not announce ground removal.");
        }
        finally
        {
            release.TrySetResult();
            await picking.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        var result = await picking.ConfigureAwait(false);
        Assert.That(result.Success, Is.True);
        Assert.That(result.StackTarget, Is.SameAs(target));
        Assert.That(map.GetObject(drop.Id), Is.Null);
        context.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        context.Verify(c => c.DeleteAsync(drop.Item), stack ? Times.Once() : Times.Never());
        if (target is not null)
        {
            Assert.That(target.Durability, Is.EqualTo(5));
        }
    }

    /// <summary>
    /// A false or ambiguous commit never permits a usable success or a second pickup.
    /// Teardown may save the same tracked outcome, without reapplying the inventory delta.
    /// </summary>
    /// <param name="stack">Whether the item merges with an existing stack.</param>
    /// <param name="throws">Whether the first commit acknowledgement throws.</param>
    /// <returns>The task.</returns>
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task UnconfirmedPickupClosesSessionAsync(bool stack, bool throws)
    {
        var (player, context, drop, target) = await CreateAsync(stack).ConfigureAwait(false);
        await using var ownedPlayer = player;
        await using var ownedDrop = drop;
        var inventory = player.Inventory!;
        var saves = 0;
        context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(() =>
        {
            if (++saves == 1)
            {
                if (throws)
                {
                    throw new InvalidOperationException("Pickup commit acknowledgement was not received.");
                }

                return new ValueTask<bool>(false);
            }

            return new ValueTask<bool>(true);
        });

        var result = await drop.TryPickUpByAsync(player).ConfigureAwait(false);
        var repeated = await drop.TryPickUpByAsync(player).ConfigureAwait(false);

        Assert.That(result.Success, Is.False);
        Assert.That(repeated.Success, Is.False);
        Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.Finished));
        Assert.That(drop.CurrentMap.GetObject(drop.Id), Is.Null);
        Assert.That(saves, Is.GreaterThanOrEqualTo(1));
        context.Verify(c => c.Attach(drop.Item), Times.Once);
        context.Verify(c => c.Detach(drop.Item), Times.Never);
        if (target is not null)
        {
            Assert.That(target.Durability, Is.EqualTo(5), "An ambiguous result must not add the quantity a second time.");
            context.Verify(c => c.DeleteAsync(drop.Item), Times.Once);
        }
        else
        {
            Assert.That(inventory.Items.Count(i => ReferenceEquals(i, drop.Item)), Is.EqualTo(1));
        }
    }

    /// <summary>
    /// Two pickup calls while a commit is blocked must not save the same outcome concurrently.
    /// </summary>
    /// <param name="stack">Whether the item merges with an existing stack.</param>
    /// <returns>The task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task ConcurrentPickupConsumesOnceAsync(bool stack)
    {
        var (player, context, drop, target) = await CreateAsync(stack).ConfigureAwait(false);
        await using var ownedPlayer = player;
        await using var ownedDrop = drop;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(async () =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return true;
        });
        var first = drop.TryPickUpByAsync(player).AsTask();
        Task<(bool Success, Item? StackTarget)>? second = null;
        try
        {
            await Task.WhenAny(first, entered.Task).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.That(entered.Task.IsCompleted, Is.True);
            second = drop.TryPickUpByAsync(player).AsTask();
        }
        finally
        {
            release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        Assert.That((await first.ConfigureAwait(false)).Success, Is.True);
        Assert.That((await second!.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)).Success, Is.False);
        context.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        if (target is not null)
        {
            Assert.That(target.Durability, Is.EqualTo(5));
        }
    }

    /// <summary>
    /// A source which could not be staged for deletion cannot increase the inventory stack.
    /// </summary>
    /// <param name="throws">Whether staging the deletion throws.</param>
    /// <returns>The task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task SourceDeletionFailureLeavesStackUnchangedAsync(bool throws)
    {
        var (player, context, drop, target) = await CreateAsync(true).ConfigureAwait(false);
        await using var ownedPlayer = player;
        await using var ownedDrop = drop;
        context.Setup(c => c.DeleteAsync(drop.Item)).Returns(() => throws
            ? throw new InvalidOperationException("Source deletion could not be staged.")
            : new ValueTask<bool>(false));

        Assert.That((await drop.TryPickUpByAsync(player).ConfigureAwait(false)).Success, Is.False);
        Assert.That(target!.Durability, Is.EqualTo(3));
        Assert.That(drop.CurrentMap.GetObject(drop.Id), Is.SameAs(drop));
        context.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        context.Verify(c => c.Detach(drop.Item), Times.Once);

        context.Setup(c => c.DeleteAsync(drop.Item)).Returns(new ValueTask<bool>(true));
        Assert.That((await drop.TryPickUpByAsync(player).ConfigureAwait(false)).Success, Is.True);
        Assert.That(target.Durability, Is.EqualTo(5));
    }

    /// <summary>
    /// A failed ground-removal notification cannot leave an unconfirmed pickup playable.
    /// </summary>
    /// <param name="throws">Whether saving throws.</param>
    /// <returns>The task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task CleanupFailureStillDisconnectsAsync(bool throws)
    {
        var (player, context, drop, _) = await CreateAsync(false).ConfigureAwait(false);
        await using var ownedPlayer = player;
        await using var ownedDrop = drop;
        context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(() => throws
            ? throw new InvalidOperationException("Pickup save was not confirmed.")
            : new ValueTask<bool>(false));
        drop.CurrentMap.ObjectRemoved += args => args.Object is DroppedItem
            ? ValueTask.FromException(new InvalidOperationException("Ground-removal subscriber failed."))
            : ValueTask.CompletedTask;

        Assert.That((await drop.TryPickUpByAsync(player).ConfigureAwait(false)).Success, Is.False);
        Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.Finished));
    }

    /// <summary>
    /// The actual full-stack transformation is persisted before successful pickup.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task FullStackTransformationParticipatesInCommitAsync()
    {
        var (player, context, drop, target) = await CreateAsync(true).ConfigureAwait(false);
        await using var ownedPlayer = player;
        await using var ownedDrop = drop;
        var signs = (B.ItemDefinition)drop.Item.Definition!;
        signs.Group = 14;
        signs.Number = 110;
        signs.Durability = 5;
        var mirror = new B.ItemDefinition { Group = 14, Number = 111, Width = 1, Height = 1, Durability = 1 };
        player.GameContext.Configuration.Items.Add(signs);
        player.GameContext.Configuration.Items.Add(mirror);
        player.GameContext.PlugInManager.RegisterPlugInAtPlugInPoint<IItemStackedPlugIn>(new SignOfDimensionsStackedPlugIn());
        context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(() =>
        {
            Assert.That(target!.Definition, Is.SameAs(mirror));
            Assert.That(target.Durability, Is.EqualTo(1));
            return new ValueTask<bool>(true);
        });

        Assert.That((await drop.TryPickUpByAsync(player).ConfigureAwait(false)).Success, Is.True);
        context.Verify(c => c.DeleteAsync(drop.Item), Times.Once);
        context.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A throwing transformation cannot expose an uncommitted quantity in an active session.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task TransformationFailureClosesSessionAsync()
    {
        var (player, _, drop, target) = await CreateAsync(true).ConfigureAwait(false);
        await using var ownedPlayer = player;
        await using var ownedDrop = drop;
        var stacked = new Mock<IItemStackedPlugIn>();
        stacked.Setup(p => p.ItemStackedAsync(player, drop.Item, target!))
            .ThrowsAsync(new InvalidOperationException("Transformation failed before save."));
        player.GameContext.PlugInManager.RegisterPlugInAtPlugInPoint(stacked.Object);

        Assert.That((await drop.TryPickUpByAsync(player).ConfigureAwait(false)).Success, Is.False);
        Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.Finished));
        Assert.That(target!.Durability, Is.EqualTo(5));
        stacked.Verify(p => p.ItemStackedAsync(player, drop.Item, target), Times.Once);
    }

    private static async ValueTask<(Player Player, Mock<IPlayerContext> Context, DroppedItem Drop, Item? Target)> CreateAsync(bool stack)
    {
        var configuration = PlayerTestHelper.CreateGameConfiguration();
        configuration.ItemDropDuration = TimeSpan.FromMinutes(5);
        var context = new Mock<IPlayerContext>();
        context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(true));
        context.Setup(c => c.DeleteAsync(It.IsAny<Item>())).Returns(new ValueTask<bool>(true));
        var provider = new Mock<IPersistenceContextProvider>();
        provider.Setup(p => p.CreateNewPlayerContext(It.IsAny<GameConfiguration>())).Returns(context.Object);
        var maps = new MapInitializer(configuration, NullLogger<MapInitializer>.Instance, NullDropGenerator.Instance, null);
        var game = new GameContext(configuration, provider.Object, maps, NullLoggerFactory.Instance,
            new PlugInManager(null, NullLoggerFactory.Instance, null, null), NullDropGenerator.Instance, new ConfigurationChangeMediator());
        maps.PlugInManager = game.PlugInManager;
        maps.PathFinderPool = game.PathFinderPool;
        var player = await PlayerTestHelper.CreatePlayerAsync(game).ConfigureAwait(false);
        var definition = new B.ItemDefinition { Width = 1, Height = 1, Durability = stack ? (byte)10 : (byte)1 };
        var item = new B.Item { Id = Guid.NewGuid(), Definition = definition, Durability = stack ? 2 : 1 };
        Item? target = null;
        if (stack)
        {
            target = new B.Item { Id = Guid.NewGuid(), Definition = definition, Durability = 3 };
            Assert.That(await player.Inventory!.AddItemAsync(12, target).ConfigureAwait(false), Is.True);
        }

        var drop = new DroppedItem(item, player.Position, player.CurrentMap!, player, player.GetAsEnumerable(), wasItemPersisted: true);
        await player.CurrentMap!.AddAsync(drop).ConfigureAwait(false);
        return (player, context, drop, target);
    }
}
