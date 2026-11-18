// <copyright file="MiniGameDisposalTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.MiniGames;

/// <summary>
/// Covers cancellation and late loop starts when mini game cleanup fails or is still in progress.
/// </summary>
[TestFixture]
public class MiniGameDisposalTests
{
    /// <summary>
    /// A failed removal must not retain timed work or prevent the map from being torn down.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task FailedRemovalStillCancelsAndTearsDownAsync()
    {
        var (game, manager) = CreateGame();
        manager.Setup(value => value.RemoveAsync(game)).ThrowsAsync(new InvalidOperationException("removal fixture rejection"));
        var token = game.EndedToken;
        var cancelled = false;
        using var registration = token.Register(() => cancelled = true);

        await game.DisposeAsync().ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(token.IsCancellationRequested, Is.True);
            Assert.That(cancelled, Is.True);
            Assert.That(game.Map.IsTornDown, Is.True);
            Assert.That(game.IsDisposed, Is.True);
            Assert.That(game.State, Is.EqualTo(MiniGameState.Disposed));
        });
    }

    /// <summary>
    /// Even a throwing cancellation callback cannot suppress the remaining callbacks or map cleanup.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task CancellationCallbackFailureStillReleasesMapAsync()
    {
        var (game, _) = CreateGame();
        var token = game.EndedToken;
        var cancelled = false;
        using var first = token.Register(() => cancelled = true);
        using var second = token.Register(() => throw new InvalidOperationException("cancellation fixture rejection"));

        await game.DisposeAsync().ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(cancelled, Is.True);
            Assert.That(token.IsCancellationRequested, Is.True);
            Assert.That(game.Map.IsTornDown, Is.True);
            Assert.That(game.IsDisposed, Is.True);
        });
    }

    /// <summary>
    /// A caller which observed the instance before removal may reach loop startup after disposal.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task LateLoopStartAfterDisposalIsIgnoredAsync()
    {
        var (game, _) = CreateGame();
        await game.DisposeAsync().ConfigureAwait(false);

        Assert.DoesNotThrow(game.EnsureGameLoopRunning);
        Assert.That(game.Map.IsTornDown, Is.True);
    }

    /// <summary>
    /// Captured end tokens remain readable after cleanup instead of dereferencing a disposed source.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task EndedTokenRemainsCancelledAfterDisposalAsync()
    {
        var (game, _) = CreateGame();
        var token = game.EndedToken;
        await game.DisposeAsync().ConfigureAwait(false);

        Assert.That(game.EndedToken, Is.EqualTo(token));
        Assert.That(game.EndedToken.IsCancellationRequested, Is.True);
    }

    /// <summary>
    /// One removal observer must not prevent later observers from releasing their map registrations.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task ThrowingRemovalObserverDoesNotSuppressOtherObserversAsync()
    {
        var (game, _) = CreateGame();
        await using var cleanup = game.ConfigureAwait(false);
        var context = new Mock<IGameContext>();
        context.SetupGet(value => value.LoggerFactory).Returns(NullLoggerFactory.Instance);
        context.SetupGet(value => value.DropGenerator).Returns(NullDropGenerator.Instance);
        var initializer = new Mock<IMapInitializer>();
        initializer.Setup(value => value.CreateGameMap(It.IsAny<GameMapDefinition>()))
            .Returns<GameMapDefinition>(map => new GameMap(map, TimeSpan.FromMinutes(1), 8));
        var manager = new MiniGameManager(context.Object, initializer.Object);
        context.SetupGet(value => value.MiniGames).Returns(manager);
        // Dispose during creation, before a loop can run, so this test has no timed background work.
        // The next initialization fails deliberately to end the caller's replacement retry.
        initializer.SetupSequence(value => value.InitializeStateAsync(It.IsAny<GameMap>()))
            .Returns(ValueTask.CompletedTask)
            .Returns(ValueTask.FromException(new InvalidOperationException("replacement fixture rejection")));
        var removed = 0;
        var retained = new HashSet<Guid>();
        manager.GameMapRemoved += (_, _) => throw new InvalidOperationException("observer fixture rejection");
        manager.GameMapRemoved += (_, map) =>
        {
            retained.Remove(map.Id);
            removed++;
        };
        manager.GameMapCreated += (_, map) =>
        {
            retained.Add(map.Id);
            var published = manager.GetOrCreateAsync(game.Definition, null!).AsTask().GetAwaiter().GetResult();
            published.DisposeAsync().AsTask().GetAwaiter().GetResult();
        };

        var exception = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.GetOrCreateAsync(game.Definition, null!).AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Is.EqualTo("replacement fixture rejection"));
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(retained, Is.Empty);
            Assert.That(manager.Maps, Is.Empty);
        });
    }

    private static (TestGame Game, Mock<IMiniGameManager> Manager) CreateGame()
    {
        var manager = new Mock<IMiniGameManager>();
        var context = new Mock<IGameContext>();
        context.SetupGet(value => value.LoggerFactory).Returns(NullLoggerFactory.Instance);
        context.SetupGet(value => value.DropGenerator).Returns(NullDropGenerator.Instance);
        context.SetupGet(value => value.MiniGames).Returns(manager.Object);
        var initializer = new Mock<IMapInitializer>();
        initializer.Setup(value => value.CreateGameMap(It.IsAny<GameMapDefinition>()))
            .Returns<GameMapDefinition>(map => new GameMap(map, TimeSpan.FromMinutes(1), 8));
        var definition = new Persistence.BasicModel.MiniGameDefinition
        {
            Type = MiniGameType.BloodCastle,
            MapCreationPolicy = MiniGameMapCreationPolicy.Shared,
            Entrance = new ExitGate { Map = new GameMapDefinition { Number = 11 } },
            EnterDuration = TimeSpan.FromMinutes(2),
            GameDuration = TimeSpan.FromMinutes(5),
        };
        return (new TestGame(new MiniGameMapKey(0, 0, string.Empty), definition, context.Object, initializer.Object), manager);
    }

    private sealed class TestGame(MiniGameMapKey key, MiniGameDefinition definition, IGameContext context, IMapInitializer initializer)
        : MiniGameContext(key, definition, context, initializer)
    {
        public CancellationToken EndedToken => this.GameEndedToken;
    }
}
