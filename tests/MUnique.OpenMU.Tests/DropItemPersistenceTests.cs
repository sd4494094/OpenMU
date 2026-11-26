// <copyright file="DropItemPersistenceTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.GameLogic.Views.Inventory;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.PlugIns;
using B = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Checks the durable handoff of an inventory item to the map before the client is told it was dropped.
/// </summary>
[TestFixture]
public class DropItemPersistenceTests
{
    private const byte Slot = 12;

    private static readonly Point Target = new(10, 10);

    /// <summary>
    /// A blocked commit exposes neither a drop nor a successful removal to the client.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task SuccessWaitsForCommitAsync()
    {
        var (player, context, _) = await CreateAsync().ConfigureAwait(false);
        await using var ownedPlayer = player;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(async () =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return true;
        });
        var view = Mock.Get(player.ViewPlugIns.GetPlugIn<IItemDropResultPlugIn>()!);
        var dropping = new DropItemAction().DropItemAsync(player, Slot, Target).AsTask();
        try
        {
            await Task.WhenAny(dropping, entered.Task).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.That(entered.Task.IsCompleted, Is.True, "The drop did not reach persistence.");
            Assert.That(player.CurrentMap!.GetDropsInRange(Target, 1), Is.Empty);
            view.Verify(p => p.ItemDropResultAsync(Slot, true), Times.Never);
        }
        finally
        {
            release.TrySetResult();
            await dropping.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        view.Verify(p => p.ItemDropResultAsync(Slot, true), Times.Once);
    }

    /// <summary>
    /// A rejected or uncertain save keeps the original object in its original slot, with no successful drop.
    /// </summary>
    /// <param name="throws">Whether the save throws rather than returning false.</param>
    /// <returns>The task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task UnconfirmedSaveRestoresOriginalItemAsync(bool throws)
    {
        var (player, context, item) = await CreateAsync().ConfigureAwait(false);
        await using var ownedPlayer = player;
        if (throws)
        {
            context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Drop commit was not confirmed."));
        }
        else
        {
            context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(false));
        }

        await new DropItemAction().DropItemAsync(player, Slot, Target).ConfigureAwait(false);

        Assert.That(player.Inventory!.GetItem(Slot), Is.SameAs(item));
        Assert.That(player.Inventory.Items, Is.EquivalentTo(new[] { item }));
        Assert.That(player.CurrentMap!.GetDropsInRange(Target, 1), Is.Empty);
        context.Verify(c => c.Detach(item), Times.Never);
        var view = Mock.Get(player.ViewPlugIns.GetPlugIn<IItemDropResultPlugIn>()!);
        view.Verify(p => p.ItemDropResultAsync(Slot, true), Times.Never);
        view.Verify(p => p.ItemDropResultAsync(Slot, false), Times.Once);
    }

    /// <summary>
    /// The result callback sees the same committed object on the floor and absent from its old storage.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task CommittedItemIsOnFloorBeforeSuccessAsync()
    {
        var (player, context, item) = await CreateAsync().ConfigureAwait(false);
        await using var ownedPlayer = player;
        var committed = false;
        context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(() =>
        {
            committed = true;
            return new ValueTask<bool>(true);
        });
        var observations = new List<(bool Committed, bool OnFloor, bool InInventory)>();
        Mock.Get(player.ViewPlugIns.GetPlugIn<IItemDropResultPlugIn>()!)
            .Setup(p => p.ItemDropResultAsync(Slot, true))
            .Callback(() => observations.Add((committed,
                player.CurrentMap!.GetDropsInRange(Target, 1).OfType<DroppedItem>().Any(d => ReferenceEquals(d.Item, item)),
                player.Inventory!.Items.Contains(item))));

        await new DropItemAction().DropItemAsync(player, Slot, Target).ConfigureAwait(false);

        Assert.That(observations, Is.EqualTo(new[] { (true, true, false) }));
        Assert.That(player.CurrentMap!.GetDropsInRange(Target, 1).OfType<DroppedItem>().Single().Item, Is.SameAs(item));
        context.Verify(c => c.Detach(item), Times.Once);
    }

    /// <summary>
    /// A retry after a rejected save hands off the same item once, without creating a second map drop.
    /// </summary>
    /// <param name="throws">Whether the first save throws rather than returning false.</param>
    /// <returns>The task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task RejectedSaveCanBeRetriedAsync(bool throws)
    {
        var (player, context, item) = await CreateAsync().ConfigureAwait(false);
        await using var ownedPlayer = player;
        var saves = 0;
        context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(() =>
        {
            if (++saves == 1 && throws)
            {
                throw new InvalidOperationException("First drop commit was not confirmed.");
            }

            return new ValueTask<bool>(saves > 1);
        });
        var action = new DropItemAction();
        await action.DropItemAsync(player, Slot, Target).ConfigureAwait(false);
        await action.DropItemAsync(player, Slot, Target).ConfigureAwait(false);

        Assert.That(saves, Is.EqualTo(2));
        Assert.That(player.Inventory!.Items, Is.Empty);
        Assert.That(player.CurrentMap!.GetDropsInRange(Target, 1).OfType<DroppedItem>().Single().Item, Is.SameAs(item));
        context.Verify(c => c.Detach(item), Times.Once);
        var view = Mock.Get(player.ViewPlugIns.GetPlugIn<IItemDropResultPlugIn>()!);
        view.Verify(p => p.ItemDropResultAsync(Slot, false), Times.Once);
        view.Verify(p => p.ItemDropResultAsync(Slot, true), Times.Once);
    }

    private static async ValueTask<(Player Player, Mock<IPlayerContext> Context, Item Item)> CreateAsync()
    {
        var configuration = PlayerTestHelper.CreateGameConfiguration();
        configuration.ItemDropDuration = TimeSpan.FromMinutes(5);
        var context = new Mock<IPlayerContext>();
        context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(true));
        context.Setup(c => c.Detach(It.IsAny<object>())).Returns(true);
        var provider = new Mock<IPersistenceContextProvider>();
        provider.Setup(p => p.CreateNewPlayerContext(It.IsAny<GameConfiguration>())).Returns(context.Object);
        var maps = new MapInitializer(configuration, NullLogger<MapInitializer>.Instance, NullDropGenerator.Instance, null);
        var game = new GameContext(configuration, provider.Object, maps, NullLoggerFactory.Instance,
            new PlugInManager(null, NullLoggerFactory.Instance, null, null), NullDropGenerator.Instance, new ConfigurationChangeMediator());
        maps.PlugInManager = game.PlugInManager;
        maps.PathFinderPool = game.PathFinderPool;
        var player = await PlayerTestHelper.CreatePlayerAsync(game).ConfigureAwait(false);
        var item = new B.Item
        {
            Id = Guid.NewGuid(),
            Definition = new B.ItemDefinition { Width = 1, Height = 1, IsDroppable = true, Durability = 1 },
            Durability = 1,
        };
        await player.Inventory!.AddItemAsync(Slot, item).ConfigureAwait(false);
        return (player, context, item);
    }
}
