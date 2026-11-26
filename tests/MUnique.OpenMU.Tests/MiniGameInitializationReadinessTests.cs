// <copyright file="MiniGameInitializationReadinessTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.MiniGames;

/// <summary>
/// Covers instance publication and cleanup while asynchronous initialization is blocked.
/// </summary>
[TestFixture]
public class MiniGameInitializationReadinessTests
{
    /// <summary>
    /// All callers await one attempt; failures leave no visible instance and allow a clean retry.
    /// </summary>
    /// <param name="reject">Whether the first initialization fails.</param>
    /// <returns>The task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task ConcurrentEntrantsAwaitInitializationAsync(bool reject)
    {
        var (manager, initializer, definition) = CreateManager();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        GameMap? attempted = null;
        initializer.Setup(value => value.InitializeStateAsync(It.IsAny<GameMap>())).Returns(async (GameMap map) =>
        {
            attempted = map;
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            if (reject)
            {
                throw new InvalidOperationException("instance initialization fixture rejection");
            }
        });
        var created = 0;
        var removed = 0;
        manager.GameMapCreated += (_, _) => created++;
        manager.GameMapRemoved += (_, _) => removed++;
        var first = manager.GetOrCreateAsync(definition, null!).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var followers = Enumerable.Range(0, 20).Select(_ => manager.GetOrCreateAsync(definition, null!).AsTask()).Append(first).ToList();
        try
        {
            Assert.That(followers.Any(task => task.IsCompleted), Is.False);
            Assert.That(manager.Maps, Is.Empty);
            Assert.That(manager.TryGetRunningMiniGame(definition, null), Is.Null);
            Assert.That(manager.GetRunningMiniGames(definition.Type), Is.Empty);
            Assert.That(created, Is.Zero);
        }
        finally
        {
            release.TrySetResult();
        }

        if (reject)
        {
            foreach (var follower in followers)
            {
                Assert.ThrowsAsync<InvalidOperationException>(async () => await follower.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
            }

            Assert.That(attempted!.IsTornDown, Is.True);
            Assert.That(manager.Maps, Is.Empty);
            Assert.That(created, Is.Zero);
            Assert.That(removed, Is.Zero, "A failed unpublished instance must not emit a removal.");
            initializer.Setup(value => value.InitializeStateAsync(It.IsAny<GameMap>())).Returns(ValueTask.CompletedTask);
            await using var retry = await manager.GetOrCreateAsync(definition, null!).ConfigureAwait(false);
            Assert.That(retry.Map, Is.Not.SameAs(attempted));
            Assert.That(retry.Map.InstanceId, Is.EqualTo(retry.Map.Id));
            Assert.That(created, Is.EqualTo(1));
            initializer.Verify(value => value.CreateGameMap(It.IsAny<GameMapDefinition>()), Times.Exactly(2));
        }
        else
        {
            var games = await Task.WhenAll(followers).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await using var game = games[0];
            Assert.That(games, Is.All.SameAs(game));
            Assert.That(manager.Maps, Is.EqualTo(new[] { game.Map }));
            Assert.That(created, Is.EqualTo(1));
            initializer.Verify(value => value.InitializeStateAsync(It.IsAny<GameMap>()), Times.Once);
        }
    }

    /// <summary>
    /// Creation observers can query and remove the instance without holding the manager lock.
    /// One observer exception cannot suppress later observers or dispose the ready instance.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task ObserversCanQueryAndRemoveWithoutDeadlockAsync()
    {
        var (manager, initializer, definition) = CreateManager();
        initializer.Setup(value => value.InitializeStateAsync(It.IsAny<GameMap>())).Returns(ValueTask.CompletedTask);
        var observed = false;
        var removed = 0;
        var notifications = 0;
        MiniGameContext? replacement = null;
        manager.GameMapCreated += (_, _) => throw new InvalidOperationException("observer fixture rejection");
        manager.GameMapCreated += (_, map) =>
        {
            if (++notifications > 1)
            {
                return;
            }

            var ready = manager.GetOrCreateAsync(definition, null!).AsTask().GetAwaiter().GetResult();
            Assert.That(ready.Map, Is.SameAs(map));
            Assert.That(manager.Maps, Does.Contain(map));
            ready.DisposeAsync().AsTask().GetAwaiter().GetResult();
            replacement = manager.GetOrCreateAsync(definition, null!).AsTask().GetAwaiter().GetResult();
            observed = true;
        };
        manager.GameMapRemoved += (_, _) =>
        {
            Assert.That(manager.Maps, Is.Empty);
            removed++;
        };
        var game = await manager.GetOrCreateAsync(definition, null!).AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.That(observed, Is.True);
        Assert.That(removed, Is.EqualTo(1));
        Assert.That(notifications, Is.EqualTo(2));
        Assert.That(game, Is.SameAs(replacement));
        Assert.That(game.IsDisposed, Is.False);
        Assert.That(manager.Maps, Is.EqualTo(new[] { game.Map }));
        await game.DisposeAsync().ConfigureAwait(false);
    }

    private static (MiniGameManager Manager, Mock<IMapInitializer> Initializer, MiniGameDefinition Definition) CreateManager()
    {
        var context = new Mock<IGameContext>();
        context.SetupGet(value => value.LoggerFactory).Returns(NullLoggerFactory.Instance);
        context.SetupGet(value => value.DropGenerator).Returns(NullDropGenerator.Instance);
        var initializer = new Mock<IMapInitializer>();
        initializer.Setup(value => value.CreateGameMap(It.IsAny<GameMapDefinition>()))
            .Returns<GameMapDefinition>(map => new GameMap(map, TimeSpan.FromMinutes(1), 8));
        var manager = new MiniGameManager(context.Object, initializer.Object);
        context.SetupGet(value => value.MiniGames).Returns(manager);
        var definition = new Persistence.BasicModel.MiniGameDefinition
        {
            Type = MiniGameType.BloodCastle,
            MapCreationPolicy = MiniGameMapCreationPolicy.Shared,
            Entrance = new ExitGate { Map = new GameMapDefinition { Number = 11 } },
            EnterDuration = TimeSpan.FromMinutes(2),
            GameDuration = TimeSpan.FromMinutes(5),
        };
        return (manager, initializer, definition);
    }
}
