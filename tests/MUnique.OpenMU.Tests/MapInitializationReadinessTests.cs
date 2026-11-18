// <copyright file="MapInitializationReadinessTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Verifies shared asynchronous map readiness, failure cleanup and independent maps.
/// </summary>
[TestFixture]
public class MapInitializationReadinessTests
{
    /// <summary>
    /// Concurrent entrants and non-creating lookups cannot observe an unfinished map.
    /// Other maps and snapshot enumeration remain usable while initialization waits.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task AllCallersAwaitOneInitializationAsync()
    {
        var initializer = new Mock<IMapInitializer>();
        var firstMap = CreateMap(0);
        var secondMap = CreateMap(1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        initializer.Setup(value => value.CreateGameMap(0)).Returns(firstMap);
        initializer.Setup(value => value.CreateGameMap(1)).Returns(secondMap);
        initializer.Setup(value => value.InitializeStateAsync(firstMap)).Returns(async () =>
        {
            entered.SetResult();
            await release.Task.ConfigureAwait(false);
        });
        initializer.Setup(value => value.InitializeStateAsync(secondMap)).Returns(ValueTask.CompletedTask);
        await using var game = CreateGame(initializer.Object);
        var notifications = new List<GameMap>();
        game.GameMapCreated += (_, map) => notifications.Add(map);
        var first = game.GetMapAsync(0).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        try
        {
            var followers = Enumerable.Range(0, 20).Select(index => game.GetMapAsync(0, createIfNotExists: index % 2 == 0).AsTask()).ToList();
            Assert.That(first.IsCompleted, Is.False);
            Assert.That(followers.Any(task => task.IsCompleted), Is.False);
            Assert.That(await game.GetMapsAsync().ConfigureAwait(false), Is.Empty);
            Assert.That(notifications, Is.Empty);
            Assert.That(await game.GetMapAsync(1).AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false), Is.SameAs(secondMap));
            Assert.That(await game.GetMapsAsync().ConfigureAwait(false), Is.EqualTo(new[] { secondMap }));
            release.SetResult();
            var results = await Task.WhenAll(followers.Append(first)).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.That(results, Is.All.SameAs(firstMap));
            Assert.That(notifications, Is.EquivalentTo(new[] { firstMap, secondMap }));
            initializer.Verify(value => value.CreateGameMap(0), Times.Once);
            initializer.Verify(value => value.InitializeStateAsync(firstMap), Times.Once);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    /// <summary>
    /// A failed initialization remains observable to its waiters; it cannot leave a
    /// broken cached map, and a later call can initialize a new instance.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task FailureTearsDownAndAllowsRetryAsync()
    {
        var initializer = new Mock<IMapInitializer>();
        var failedMap = CreateMap(0);
        var retryMap = CreateMap(0);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        initializer.SetupSequence(value => value.CreateGameMap(0)).Returns(failedMap).Returns(retryMap);
        initializer.Setup(value => value.InitializeStateAsync(failedMap)).Returns(async () =>
        {
            entered.SetResult();
            await release.Task.ConfigureAwait(false);
            throw new InvalidOperationException("initialization fixture rejection");
        });
        initializer.Setup(value => value.InitializeStateAsync(retryMap)).Returns(ValueTask.CompletedTask);
        await using var game = CreateGame(initializer.Object);
        var first = game.GetMapAsync(0).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var follower = game.GetMapAsync(0, false).AsTask();
        release.SetResult();
        Assert.ThrowsAsync<InvalidOperationException>(async () => await first.ConfigureAwait(false));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await follower.ConfigureAwait(false));
        Assert.That(failedMap.IsTornDown, Is.True);
        Assert.That(await game.GetMapsAsync().ConfigureAwait(false), Is.Empty);
        Assert.That(await game.GetMapAsync(0, false).ConfigureAwait(false), Is.Null);
        Assert.That(await game.GetMapAsync(0).ConfigureAwait(false), Is.SameAs(retryMap));
        initializer.Verify(value => value.CreateGameMap(0), Times.Exactly(2));
    }

    /// <summary>
    /// A map not currently configured is not cached forever as absent.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task MissingMapCanBecomeAvailableAsync()
    {
        var initializer = new Mock<IMapInitializer>();
        var map = CreateMap(0);
        initializer.SetupSequence(value => value.CreateGameMap(0)).Returns((GameMap?)null).Returns(map);
        initializer.Setup(value => value.InitializeStateAsync(map)).Returns(ValueTask.CompletedTask);
        await using var game = CreateGame(initializer.Object);
        Assert.That(await game.GetMapAsync(0, false).ConfigureAwait(false), Is.Null);
        initializer.Verify(value => value.CreateGameMap(0), Times.Never);
        Assert.That(await game.GetMapAsync(0).ConfigureAwait(false), Is.Null);
        Assert.That(await game.GetMapAsync(0).ConfigureAwait(false), Is.SameAs(map));
    }

    /// <summary>
    /// Publication observers can synchronously resolve the fully initialized map;
    /// one observer failure cannot tear it down or suppress another observer.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task CreatedObserversCanResolveMapAndFailIndependentlyAsync()
    {
        var initializer = new Mock<IMapInitializer>();
        var map = CreateMap(0);
        initializer.Setup(value => value.CreateGameMap(0)).Returns(map);
        initializer.Setup(value => value.InitializeStateAsync(map)).Returns(ValueTask.CompletedTask);
        await using var game = CreateGame(initializer.Object);
        var observed = false;
        game.GameMapCreated += (_, _) => throw new InvalidOperationException("observer fixture rejection");
        game.GameMapCreated += (_, created) =>
        {
            Assert.That(game.GetMapAsync(0, false).AsTask().GetAwaiter().GetResult(), Is.SameAs(created));
            Assert.That(game.GetMapsAsync().AsTask().GetAwaiter().GetResult(), Does.Contain(created));
            observed = true;
        };

        Assert.That(await game.GetMapAsync(0).AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false), Is.SameAs(map));
        Assert.That(observed, Is.True);
        Assert.That(map.IsTornDown, Is.False);
        initializer.Verify(value => value.CreateGameMap(0), Times.Once);
    }

    /// <summary>
    /// Initialization which finishes after context disposal cannot publish a live map.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task DisposalDuringInitializationRejectsPublicationAsync()
    {
        var initializer = new Mock<IMapInitializer>();
        var map = CreateMap(0);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        initializer.Setup(value => value.CreateGameMap(0)).Returns(map);
        initializer.Setup(value => value.InitializeStateAsync(map)).Returns(async () =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
        });
        var game = CreateGame(initializer.Object);
        var published = false;
        game.GameMapCreated += (_, _) => published = true;
        var creating = game.GetMapAsync(0).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await game.DisposeAsync().ConfigureAwait(false);
        release.TrySetResult();
        Assert.ThrowsAsync<ObjectDisposedException>(async () => await creating.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
        Assert.That(published, Is.False);
        Assert.That(map.IsTornDown, Is.True);
        Assert.That(await game.GetMapsAsync().ConfigureAwait(false), Is.Empty);
        Assert.ThrowsAsync<ObjectDisposedException>(async () => await game.GetMapAsync(0).ConfigureAwait(false));
    }

    private static GameMap CreateMap(short number) => new(new GameMapDefinition
    {
        Number = number,
        TerrainData = new byte[ushort.MaxValue + 3],
    }, TimeSpan.FromMinutes(1), 8);

    private static GameContext CreateGame(IMapInitializer initializer)
    {
        var configuration = new GameConfiguration { RecoveryInterval = int.MaxValue, MaximumPartySize = 5 };
        var game = new GameContext(configuration, new InMemoryPersistenceContextProvider(), initializer, NullLoggerFactory.Instance,
            new PlugInManager(null, NullLoggerFactory.Instance, null, null), NullDropGenerator.Instance, new ConfigurationChangeMediator());
        game.StopPeriodicTasks();
        return game;
    }
}
