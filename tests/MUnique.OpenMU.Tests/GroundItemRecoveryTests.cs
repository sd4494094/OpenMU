// <copyright file="GroundItemRecoveryTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.PlugIns;
using B = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Verifies durable publication, owner identity and uncertain-pickup recovery fences.
/// </summary>
[TestFixture]
public class GroundItemRecoveryTests
{
    /// <summary>
    /// Concurrent readers and a delayed snapshot cannot duplicate a consumed generation.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task ConcurrentPublicationAndConsumedSnapshotAsync()
    {
        await using var fixture = await CreateAsync().ConfigureAwait(false);
        var item = fixture.CreateItem();
        var snapshot = Copy(item);
        var publications = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => fixture.Map.PublishGroundItemAsync(Copy(item), fixture.Game, readStartedAt: Stopwatch.GetTimestamp()).AsTask())).ConfigureAwait(false);
        Assert.That(publications.Count(value => value), Is.EqualTo(1));
        var drop = fixture.Drop;
        Assert.That((await drop.TryPickUpByAsync(fixture.Player).ConfigureAwait(false)).Success, Is.True);
        Assert.That(drop.Item.GroundDelivery, Is.Null);
        fixture.Context.Verify(context => context.DeleteAsync(It.IsAny<GroundItemDelivery>()), Times.Once);
        Assert.That(await fixture.Map.PublishGroundItemAsync(snapshot, fixture.Game, readStartedAt: Stopwatch.GetTimestamp()).ConfigureAwait(false), Is.False);
        Assert.That(fixture.Map.GetDropsInRange(fixture.Player.Position, 1), Is.Empty);
    }

    /// <summary>
    /// Late publication cleanup cannot remove a different drop which reused its identifier.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task LateCleanupPreservesReusedDropIdentifierAsync()
    {
        await using var fixture = await CreateAsync().ConfigureAwait(false);
        await fixture.Map.PublishGroundItemAsync(fixture.CreateItem(), fixture.Game).ConfigureAwait(false);
        var oldDrop = fixture.Drop;
        await oldDrop.DisposeAsync().ConfigureAwait(false);
        await fixture.Map.PublishGroundItemAsync(fixture.CreateItem(), fixture.Game).ConfigureAwait(false);
        var replacement = fixture.Drop;
        oldDrop.Id = replacement.Id; // Simulate cleanup arriving after identifier reuse.
        await fixture.Map.RemoveAsync(oldDrop).ConfigureAwait(false);
        Assert.That(fixture.Map.GetDrop(replacement.Id), Is.SameAs(replacement));
    }

    /// <summary>
    /// Inline map observers can fail pickup without deadlocking the publication lock.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task InlineObserverFailedPickupDoesNotDeadlockAsync()
    {
        await using var fixture = await CreateAsync().ConfigureAwait(false);
        var item = fixture.CreateItem();
        fixture.Context.Setup(context => context.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(false));
        fixture.Map.ObjectAdded += async args =>
        {
            if (args.Object is DroppedItem drop)
            {
                Assert.That((await drop.TryPickUpByAsync(fixture.Player).ConfigureAwait(false)).Success, Is.False);
            }
        };
        await fixture.Map.PublishGroundItemAsync(item, fixture.Game).AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.That(fixture.Player.PersistenceContextReleasedAt, Is.GreaterThan(0));
    }

    /// <summary>
    /// A notification failure after actual removal leaves an untouched delivery retryable.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task FailedPublicationAndCleanupCanRetryAsync()
    {
        await using var fixture = await CreateAsync().ConfigureAwait(false);
        var item = fixture.CreateItem();
        var fail = true;
        fixture.Map.ObjectAdded += args => fail && args.Object is DroppedItem
            ? ValueTask.FromException(new InvalidOperationException("Add observer failed.")) : ValueTask.CompletedTask;
        fixture.Map.ObjectRemoved += args => fail && args.Object is DroppedItem
            ? ValueTask.FromException(new InvalidOperationException("Remove observer failed.")) : ValueTask.CompletedTask;
        Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Map.PublishGroundItemAsync(item, fixture.Game).ConfigureAwait(false));
        fail = false;
        Assert.That(await fixture.Map.PublishGroundItemAsync(Copy(item), fixture.Game).ConfigureAwait(false), Is.True);
    }

    /// <summary>
    /// A late add observer failure cannot revoke a consumption or uncertain-pickup fence.
    /// </summary>
    /// <param name="confirmed">Whether the inline pickup commit succeeds.</param>
    /// <returns>The task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task ObserverFailurePreservesPickupFenceAsync(bool confirmed)
    {
        await using var fixture = await CreateAsync().ConfigureAwait(false);
        var item = fixture.CreateItem();
        var stale = Copy(item);
        var readStartedAt = Stopwatch.GetTimestamp();
        fixture.Context.Setup(context => context.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(confirmed));
        var fail = true;
        fixture.Map.ObjectAdded += async args =>
        {
            if (fail && args.Object is DroppedItem drop)
            {
                await drop.TryPickUpByAsync(fixture.Player).ConfigureAwait(false);
                throw new InvalidOperationException("Observer failed after pickup.");
            }
        };
        Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Map.PublishGroundItemAsync(item, fixture.Game).ConfigureAwait(false));
        fail = false;
        Assert.That(await fixture.Map.PublishGroundItemAsync(Copy(stale), fixture.Game, readStartedAt: readStartedAt).ConfigureAwait(false), Is.False);
        Assert.That(await fixture.Map.PublishGroundItemAsync(Copy(stale), fixture.Game, readStartedAt: Stopwatch.GetTimestamp()).ConfigureAwait(false), Is.EqualTo(!confirmed));
    }

    /// <summary>
    /// Teardown during an add notification removes the late drop and makes old references inert.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task TeardownDuringPublicationCannotLeavePickableDropAsync()
    {
        await using var fixture = await CreateAsync().ConfigureAwait(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DroppedItem? oldDrop = null;
        fixture.Map.ObjectAdded += async args =>
        {
            if (args.Object is DroppedItem drop)
            {
                oldDrop = drop;
                entered.TrySetResult();
                await release.Task.ConfigureAwait(false);
            }
        };
        var publishing = fixture.Map.PublishGroundItemAsync(fixture.CreateItem(), fixture.Game).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await fixture.Map.TearDownAsync().ConfigureAwait(false);
        }
        finally
        {
            release.TrySetResult();
            await publishing.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        Assert.That(await publishing.ConfigureAwait(false), Is.False);
        Assert.That(fixture.Map.GetDropsInRange(default, 256), Is.Empty);
        Assert.That((await oldDrop!.TryPickUpByAsync(fixture.Player).ConfigureAwait(false)).Success, Is.False);
    }

    /// <summary>
    /// Host shutdown waits for a map readiness reader as well as periodic host scans.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task StopWaitsForMapRestoreReaderAsync()
    {
        await using var fixture = await CreateAsync().ConfigureAwait(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Context.Setup(context => context.GetGroundItemPageAsync(It.IsAny<GroundItemQuery>(), It.IsAny<CancellationToken>())).Returns(async (GroundItemQuery query, CancellationToken _) =>
        {
            if (query.MapDefinitionId is not null)
            {
                entered.TrySetResult();
                await release.Task.ConfigureAwait(false);
            }

            return Array.Empty<Item>();
        });
        var recovery = new GroundItemRecovery(fixture.Game, fixture.Host);
        await recovery.StartAsync().ConfigureAwait(false);
        var restoring = recovery.RestoreMapAsync(fixture.Map).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var stopping = recovery.StopAsync().AsTask();
        try
        {
            Assert.That(stopping.IsCompleted, Is.False, "Shutdown returned while a map reader remained in flight.");
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(restoring, stopping).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// An uncertain outcome permits only a fresh query after the old context was released.
    /// </summary>
    /// <param name="releaseFails">Whether disposing the old context is initially rejected.</param>
    /// <returns>The task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task UnconfirmedPickupRequiresReleasedContextAndFreshReadAsync(bool releaseFails)
    {
        await using var fixture = await CreateAsync().ConfigureAwait(false);
        var item = fixture.CreateItem();
        var stale = Copy(item);
        await fixture.Map.PublishGroundItemAsync(item, fixture.Game).ConfigureAwait(false);
        var readBeforeRelease = Stopwatch.GetTimestamp();
        fixture.Context.Setup(context => context.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(false));
        var released = false;
        fixture.Context.Setup(context => context.Dispose()).Callback(() =>
        {
            if (releaseFails && !released)
            {
                throw new InvalidOperationException("Old tracker has not been released.");
            }

            released = true;
        });

        if (releaseFails)
        {
            Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Drop.TryPickUpByAsync(fixture.Player).ConfigureAwait(false));
            Assert.That(fixture.Player.PersistenceContextReleasedAt, Is.Zero);
            Assert.That(await fixture.Map.PublishGroundItemAsync(Copy(stale), fixture.Game, readStartedAt: Stopwatch.GetTimestamp()).ConfigureAwait(false), Is.False);
            released = true;
            await fixture.Player.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            Assert.That((await fixture.Drop.TryPickUpByAsync(fixture.Player).ConfigureAwait(false)).Success, Is.False);
        }

        Assert.That(fixture.Player.PersistenceContextReleasedAt, Is.GreaterThan(0));
        Assert.That(await fixture.Map.PublishGroundItemAsync(Copy(stale), fixture.Game, readStartedAt: readBeforeRelease).ConfigureAwait(false), Is.False);
        Assert.That(await fixture.Map.PublishGroundItemAsync(Copy(stale), fixture.Game).ConfigureAwait(false), Is.False, "An action replay is not a fresh recovery read.");
        Assert.That(await fixture.Map.PublishGroundItemAsync(Copy(stale), fixture.Game, readStartedAt: Stopwatch.GetTimestamp()).ConfigureAwait(false), Is.True);
        Assert.That(fixture.Drop.Item.GroundDelivery!.ExpiresAt, Is.EqualTo(stale.GroundDelivery!.ExpiresAt));
    }

    /// <summary>
    /// Owner priority and character binding apply before either normal pickup or stacking.
    /// </summary>
    /// <param name="bound">Whether the item is character bound after priority expires.</param>
    /// <param name="stack">Whether a matching inventory stack exists.</param>
    /// <returns>The task.</returns>
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task OwnerIdentitySurvivesReconnectAsync(bool bound, bool stack)
    {
        await using var fixture = await CreateAsync().ConfigureAwait(false);
        var item = fixture.CreateItem();
        item.Definition!.IsBoundToCharacter = bound;
        item.Definition.Durability = stack ? (byte)10 : (byte)1;
        if (bound)
        {
            item.GroundDelivery!.OwnerPriorityUntil = DateTime.UtcNow.AddSeconds(-1);
        }

        item.GroundDelivery!.OwnerCharacterId = Guid.NewGuid();
        Item? target = null;
        if (stack)
        {
            target = new B.Item { Definition = item.Definition, Durability = 3 };
            await fixture.Player.Inventory!.AddItemAsync(12, target).ConfigureAwait(false);
        }

        await fixture.Map.PublishGroundItemAsync(item, fixture.Game).ConfigureAwait(false);
        Assert.That((await fixture.Drop.TryPickUpByAsync(fixture.Player).ConfigureAwait(false)).Success, Is.False);
        Assert.That(target?.Durability ?? 3, Is.EqualTo(3));
        await using var reconnected = await PlayerTestHelper.CreatePlayerAsync(fixture.Game, item.GroundDelivery.OwnerCharacterId).ConfigureAwait(false);
        Assert.That(fixture.Drop.IsPlayerAnOwner(reconnected), Is.True);
        Assert.That((await fixture.Drop.TryPickUpByAsync(reconnected).ConfigureAwait(false)).Success, Is.True);
    }

    /// <summary>
    /// A delivery never crosses an actual host, map or instance boundary.
    /// </summary>
    /// <param name="boundary">The mismatched address component.</param>
    /// <returns>The task.</returns>
    [TestCase("host")]
    [TestCase("map")]
    [TestCase("instance")]
    public async Task RejectsWrongAddressAsync(string boundary)
    {
        await using var fixture = await CreateAsync().ConfigureAwait(false);
        var item = fixture.CreateItem();
        switch (boundary)
        {
            case "host": item.GroundDelivery!.HostServerId = Guid.NewGuid(); break;
            case "map": item.GroundDelivery!.MapDefinitionId = Guid.NewGuid(); break;
            case "instance": item.GroundDelivery!.MapInstanceId = Guid.NewGuid(); break;
        }

        Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Map.PublishGroundItemAsync(item, fixture.Game).ConfigureAwait(false));
        Assert.That(fixture.Map.GetDropsInRange(fixture.Player.Position, 1), Is.Empty);
    }

    /// <summary>
    /// An original expiry is neither renewed by publication nor extended by recovery.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task OriginalExpiryRejectsLatePublicationAndPickupAsync()
    {
        await using var fixture = await CreateAsync().ConfigureAwait(false);
        var item = fixture.CreateItem();
        item.GroundDelivery!.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        Assert.That(await fixture.Map.PublishGroundItemAsync(item, fixture.Game).ConfigureAwait(false), Is.False);
        await using var lateDrop = new DroppedItem(item, fixture.Map, fixture.Game, null);
        await fixture.Map.AddAsync(lateDrop).ConfigureAwait(false);
        Assert.That((await lateDrop.TryPickUpByAsync(fixture.Player).ConfigureAwait(false)).Success, Is.False);
        fixture.Context.Verify(context => context.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Host scans expire offline aggregates and leave old instances out of ordinary maps.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task OfflineExpiryAndOldInstanceBoundaryAsync()
    {
        await using var fixture = await CreateAsync().ConfigureAwait(false);
        var expired = fixture.CreateItem();
        expired.GroundDelivery!.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        var oldInstance = fixture.CreateItem();
        oldInstance.GroundDelivery!.MapInstanceId = Guid.NewGuid();
        var live = fixture.CreateItem();
        var queries = new List<GroundItemQuery>();
        fixture.Context.Setup(context => context.GetGroundItemPageAsync(It.IsAny<GroundItemQuery>(), It.IsAny<CancellationToken>())).Returns((GroundItemQuery query, CancellationToken _) =>
        {
            queries.Add(query);
            return new ValueTask<IReadOnlyList<Item>>(query.ExpiresBeforeUtc is not null ? new[] { expired } : new[] { oldInstance, live });
        });
        var trade = new Mock<ITradeContext>();
        trade.Setup(context => context.DeleteAsync((Item)expired)).Returns(new ValueTask<bool>(true));
        trade.Setup(context => context.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(true));
        fixture.Provider.Setup(provider => provider.CreateNewTradeContext()).Returns(trade.Object);
        var recovery = new GroundItemRecovery(fixture.Game, fixture.Host);
        try
        {
            await recovery.StartAsync().ConfigureAwait(false);
            Assert.That(queries, Has.Count.EqualTo(2));
            Assert.That(queries.All(query => query.HostServerId == fixture.Host.Id && query.IncludeAllInstances && query.Limit <= 256), Is.True);
            Assert.That(fixture.Drop.Item, Is.SameAs(live));
            trade.Verify(context => context.Attach(expired), Times.Once);
            trade.Verify(context => context.DeleteAsync((Item)expired), Times.Once);
            trade.Verify(context => context.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            await recovery.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A failed expiry commit is retried in a later bounded host cycle.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task ExpiryFailureCanRetryAsync()
    {
        await using var fixture = await CreateAsync().ConfigureAwait(false);
        var item = fixture.CreateItem();
        item.GroundDelivery!.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        fixture.Context.Setup(context => context.GetGroundItemPageAsync(It.IsAny<GroundItemQuery>(), It.IsAny<CancellationToken>())).Returns((GroundItemQuery query, CancellationToken _) =>
            new ValueTask<IReadOnlyList<Item>>(query.ExpiresBeforeUtc is not null ? new[] { item } : Array.Empty<Item>()));
        var saves = 0;
        var trade = new Mock<ITradeContext>();
        trade.Setup(context => context.DeleteAsync((Item)item)).Returns(new ValueTask<bool>(true));
        trade.Setup(context => context.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(() => new ValueTask<bool>(++saves > 1));
        fixture.Provider.Setup(provider => provider.CreateNewTradeContext()).Returns(trade.Object);
        var recovery = new GroundItemRecovery(fixture.Game, fixture.Host);
        try
        {
            await recovery.StartAsync().ConfigureAwait(false);
            Assert.That(saves, Is.EqualTo(1));
            await recovery.ScanAsync().ConfigureAwait(false);
            Assert.That(saves, Is.EqualTo(2));
        }
        finally
        {
            await recovery.StopAsync().ConfigureAwait(false);
        }
    }

    private static B.Item Copy(Item item)
    {
        var marker = item.GroundDelivery!;
        return new B.Item
        {
            Id = item.GetId(), Definition = item.Definition, Durability = item.Durability,
            GroundDelivery = new B.GroundItemDelivery
            {
                Id = marker.GetId(), HostServerId = marker.HostServerId, MapDefinitionId = marker.MapDefinitionId,
                MapInstanceId = marker.MapInstanceId, OwnerCharacterId = marker.OwnerCharacterId,
                PositionX = marker.PositionX, PositionY = marker.PositionY, DroppedAt = marker.DroppedAt,
                OwnerPriorityUntil = marker.OwnerPriorityUntil, ExpiresAt = marker.ExpiresAt,
            },
        };
    }

    private static async ValueTask<Fixture> CreateAsync()
    {
        var config = PlayerTestHelper.CreateGameConfiguration();
        config.ItemDropDuration = TimeSpan.FromMinutes(5);
        config.Maps.Clear();
        var definition = new B.GameMapDefinition { Id = Guid.NewGuid(), IsActive = true, TerrainData = new byte[ushort.MaxValue + 3] };
        config.Maps.Add(definition);
        var context = new Mock<IPlayerContext>();
        context.Setup(value => value.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(true));
        context.Setup(value => value.DeleteAsync(It.IsAny<GroundItemDelivery>())).Returns(new ValueTask<bool>(true));
        context.Setup(value => value.DeleteAsync(It.IsAny<Item>())).Returns(new ValueTask<bool>(true));
        context.Setup(value => value.CreateNew<GroundItemDelivery>(It.IsAny<object?[]>())).Returns(() => new B.GroundItemDelivery { Id = Guid.NewGuid() });
        var provider = new Mock<IPersistenceContextProvider>();
        provider.Setup(value => value.CreateNewPlayerContext(It.IsAny<GameConfiguration>())).Returns(context.Object);
        var maps = new MapInitializer(config, NullLogger<MapInitializer>.Instance, NullDropGenerator.Instance, null);
        var game = new GameContext(config, provider.Object, maps, NullLoggerFactory.Instance,
            new PlugInManager(null, NullLoggerFactory.Instance, null, null), NullDropGenerator.Instance, new ConfigurationChangeMediator());
        maps.PlugInManager = game.PlugInManager;
        maps.PathFinderPool = game.PathFinderPool;
        var player = await PlayerTestHelper.CreatePlayerAsync(game, Guid.NewGuid()).ConfigureAwait(false);
        var host = new B.GameServerDefinition { Id = Guid.NewGuid(), ServerConfiguration = new B.GameServerConfiguration() };
        host.ServerConfiguration.Maps.Add(definition);
        player.CurrentMap!.BindHost(host);
        return new Fixture(game, player, player.CurrentMap, host, context, provider);
    }

    private sealed record Fixture(GameContext Game, Player Player, GameMap Map, B.GameServerDefinition Host, Mock<IPlayerContext> Context, Mock<IPersistenceContextProvider> Provider) : IAsyncDisposable
    {
        public DroppedItem Drop => this.Map.GetDropsInRange(default, 256).OfType<DroppedItem>().Single();

        public B.Item CreateItem()
        {
            var item = new B.Item { Id = Guid.NewGuid(), Definition = new B.ItemDefinition { Width = 1, Height = 1, Durability = 1 }, Durability = 1 };
            this.Map.StageGroundDelivery(item, this.Context.Object, this.Player.SelectedCharacter!.GetId(), this.Player.Position);
            return item;
        }

        public async ValueTask DisposeAsync()
        {
            await this.Player.DisposeAsync().ConfigureAwait(false);
            await this.Game.DisposeAsync().ConfigureAwait(false);
        }
    }
}
