// <copyright file="MiniGameManager.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.MiniGames;

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using MUnique.OpenMU.GameLogic.MiniGames.Doppelganger;
using MUnique.OpenMU.GameLogic.MiniGames.ImperialGuardian;
using MUnique.OpenMU.GameLogic.MiniGames.Kanturu;
using Nito.AsyncEx;

/// <summary>
/// Hosts and tracks the mini game instances of a game context.
/// </summary>
public sealed class MiniGameManager : IMiniGameManager
{
    private static readonly Meter Meter = new(GameContext.MeterName);

    private static readonly Counter<int> MiniGameCounter = Meter.CreateCounter<int>("MiniGameCount");

    private readonly IGameContext _gameContext;
    private readonly IMapInitializer _mapInitializer;
    private readonly ConcurrentDictionary<MiniGameMapKey, MiniGameContext> _miniGames = new();
    private readonly ConcurrentDictionary<MiniGameMapKey, AsyncLazy<MiniGameContext>> _initializations = new();
    private readonly ConcurrentDictionary<Guid, bool> _publishedMaps = new();
    private readonly AsyncLock _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="MiniGameManager"/> class.
    /// </summary>
    /// <param name="gameContext">The game context to which the hosted games belong.</param>
    /// <param name="mapInitializer">The map initializer.</param>
    public MiniGameManager(IGameContext gameContext, IMapInitializer mapInitializer)
    {
        this._gameContext = gameContext;
        this._mapInitializer = mapInitializer;
    }

    /// <inheritdoc />
    public event EventHandler<GameMap>? GameMapCreated;

    /// <inheritdoc />
    public event EventHandler<GameMap>? GameMapRemoved;

    /// <inheritdoc />
    public IReadOnlyList<GameMap> Maps => this._miniGames.Values.Select(game => game.Map).ToList();

    /// <inheritdoc />
    public async ValueTask<MiniGameContext> GetOrCreateAsync(MiniGameDefinition miniGameDefinition, Player requester)
    {
        var miniGameKey = MiniGameMapKey.Create(miniGameDefinition, requester);
        while (true)
        {
            if (this._miniGames.TryGetValue(miniGameKey, out var miniGameContext) && miniGameContext is { IsDisposed: false, IsDisposing: false })
            {
                return miniGameContext;
            }

            var initialization = this._initializations.GetOrAdd(
                miniGameKey,
                key =>
                {
                    AsyncLazy<MiniGameContext>? own = null;
                    own = new AsyncLazy<MiniGameContext>(() => this.CreateAndInitializeAsync(key, miniGameDefinition, own!).AsTask());
                    return own;
                });
            try
            {
                var game = await initialization.Task.ConfigureAwait(false);
                if (!game.IsDisposed && !game.IsDisposing)
                {
                    return game;
                }
            }
            finally
            {
                this._initializations.TryRemove(new KeyValuePair<MiniGameMapKey, AsyncLazy<MiniGameContext>>(miniGameKey, initialization));
            }
        }
    }

    /// <inheritdoc />
    public MiniGameContext? TryGetRunningMiniGame(MiniGameDefinition miniGameDefinition, Player? requester)
    {
        if (requester is null && miniGameDefinition.MapCreationPolicy != MiniGameMapCreationPolicy.Shared)
        {
            // The key of per-player and per-party games is derived from the requester.
            return null;
        }

        var miniGameKey = MiniGameMapKey.Create(miniGameDefinition, requester!);
        if (this._miniGames.TryGetValue(miniGameKey, out var miniGameContext)
            && miniGameContext is { IsDisposed: false, IsDisposing: false })
        {
            return miniGameContext;
        }

        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<MiniGameContext> GetRunningMiniGames(MiniGameType miniGameType)
    {
        return this._miniGames.Values
            .Where(game => game is { IsDisposed: false, IsDisposing: false } && game.Definition.Type == miniGameType)
            .ToList();
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryRejoinAsync(Player player)
    {
        foreach (var game in this._miniGames.Values)
        {
            if (game is { IsDisposed: false, IsDisposing: false }
                && await game.TryRejoinAsync(player).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public async ValueTask RemoveAsync(MiniGameContext miniGameContext)
    {
        miniGameContext.Dispose();
        using (await this._lock.LockAsync().ConfigureAwait(false))
        {
            // A replaced instance must not unregister its successor.
            if (this._miniGames.TryGetValue(miniGameContext.Key, out var current)
                && ReferenceEquals(current, miniGameContext)
                && this._miniGames.TryRemove(miniGameContext.Key, out _))
            {
                MiniGameCounter.Add(-1);
            }
        }

        // Unpublished failures have no removal notification. A replaced but
        // previously published instance still needs its own removal notification.
        if (this._publishedMaps.TryRemove(miniGameContext.Map.Id, out _))
        {
            foreach (var subscriber in this.GameMapRemoved?.GetInvocationList() ?? [])
            {
                try
                {
                    ((EventHandler<GameMap>)subscriber)(this, miniGameContext.Map);
                }
                catch (Exception exception)
                {
                    this._gameContext.LoggerFactory.CreateLogger<MiniGameManager>()
                        .LogError(exception, "A subscriber of mini game map removal failed.");
                }
            }
        }
    }

    private async ValueTask<MiniGameContext> CreateAndInitializeAsync(MiniGameMapKey miniGameKey, MiniGameDefinition miniGameDefinition, AsyncLazy<MiniGameContext> initialization)
    {
        // Another caller may have published a map between our caller's fast lookup
        // and installation of this lazy. Never replace that live instance.
        if (this._miniGames.TryGetValue(miniGameKey, out var ready) && ready is { IsDisposed: false, IsDisposing: false })
        {
            return ready;
        }

        MiniGameContext miniGameContext;
        switch (miniGameDefinition.Type)
        {
            case MiniGameType.ChaosCastle:
                miniGameContext = new ChaosCastleContext(miniGameKey, miniGameDefinition, this._gameContext, this._mapInitializer);
                break;
            case MiniGameType.DevilSquare:
                miniGameContext = new DevilSquareContext(miniGameKey, miniGameDefinition, this._gameContext, this._mapInitializer);
                break;
            case MiniGameType.BloodCastle:
                miniGameContext = new BloodCastleContext(miniGameKey, miniGameDefinition, this._gameContext, this._mapInitializer);
                break;
            case MiniGameType.Doppelganger:
                miniGameContext = new DoppelgangerContext(miniGameKey, miniGameDefinition, this._gameContext, this._mapInitializer);
                break;
            case MiniGameType.Kanturu:
                miniGameContext = new KanturuContext(miniGameKey, miniGameDefinition, this._gameContext, this._mapInitializer);
                break;
            case MiniGameType.IllusionTemple:
                miniGameContext = new IllusionTempleContext(miniGameKey, miniGameDefinition, this._gameContext, this._mapInitializer);
                break;
            case MiniGameType.ImperialGuardian:
                miniGameContext = new ImperialGuardianContext(miniGameKey, miniGameDefinition, this._gameContext, this._mapInitializer);
                break;
            default:
                miniGameContext = new MiniGameContext(miniGameKey, miniGameDefinition, this._gameContext, this._mapInitializer);
                break;
        }

        try
        {
            await this._mapInitializer.InitializeStateAsync(miniGameContext.Map).ConfigureAwait(false);
            using (await this._lock.LockAsync().ConfigureAwait(false))
            {
                if (this._miniGames.TryGetValue(miniGameKey, out var previous))
                {
                    this._miniGames.TryRemove(new KeyValuePair<MiniGameMapKey, MiniGameContext>(miniGameKey, previous));
                    MiniGameCounter.Add(-1);
                }

                this._miniGames[miniGameKey] = miniGameContext;
                this._publishedMaps[miniGameContext.Map.Id] = true;
                MiniGameCounter.Add(1);
            }
        }
        catch
        {
            await miniGameContext.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        // Observers may remove this instance and synchronously request another one.
        // Remove only our own pending generation before invoking them.
        this._initializations.TryRemove(new KeyValuePair<MiniGameMapKey, AsyncLazy<MiniGameContext>>(miniGameKey, initialization));
        foreach (var subscriber in this.GameMapCreated?.GetInvocationList() ?? [])
        {
            if (miniGameContext.IsDisposed || miniGameContext.IsDisposing)
            {
                break;
            }

            try
            {
                ((EventHandler<GameMap>)subscriber)(this, miniGameContext.Map);
            }
            catch (Exception exception)
            {
                this._gameContext.LoggerFactory.CreateLogger<MiniGameManager>()
                    .LogError(exception, "A subscriber of mini game map creation failed.");
            }
        }

        // The loop starts here and not in the constructor, so overridden members
        // read their post-construction values from the first tick on.
        if (!miniGameContext.IsDisposed && !miniGameContext.IsDisposing)
        {
            miniGameContext.EnsureGameLoopRunning();
        }

        return miniGameContext;
    }
}
