// <copyright file="GroundItemRecovery.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic;

using System.Diagnostics;
using System.Threading;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.Persistence;

/// <summary>
/// Restores outstanding deliveries and expires their original aggregates without an online owner.
/// </summary>
public sealed class GroundItemRecovery
{
    private readonly IGameContext _gameContext;
    private readonly GameServerDefinition _host;
    private readonly ILogger<GroundItemRecovery> _logger;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly object _restoreLifecycle = new();
    private volatile bool _active;
    private int _restoringMaps;
    private TaskCompletionSource? _restoresIdle;
    private DeliveryCursor? _liveCursor;
    private DeliveryCursor? _expiredCursor;

    /// <summary>
    /// Initializes a new instance of the <see cref="GroundItemRecovery"/> class.
    /// </summary>
    /// <param name="gameContext">The actual host context.</param>
    /// <param name="host">The persistent definition of the actual host.</param>
    public GroundItemRecovery(IGameContext gameContext, GameServerDefinition host)
    {
        this._gameContext = gameContext;
        this._host = host;
        this._logger = gameContext.LoggerFactory.CreateLogger<GroundItemRecovery>();
    }

    /// <summary>
    /// Starts recovery before listeners accept players and scans one bounded host page.
    /// </summary>
    /// <returns>The initial scan.</returns>
    public async ValueTask StartAsync()
    {
        if (this._host.GetId() == Guid.Empty)
        {
            throw new InvalidOperationException("Ground recovery requires a persistent host identity.");
        }

        this._active = true;
        await this.ScanAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Stops new scans and waits for the current host scan to leave its contexts.
    /// </summary>
    /// <returns>The stop operation.</returns>
    public async ValueTask StopAsync()
    {
        Task restores;
        lock (this._restoreLifecycle)
        {
            this._active = false;
            restores = this._restoresIdle?.Task ?? Task.CompletedTask;
        }

        await this._scanGate.WaitAsync().ConfigureAwait(false);
        this._scanGate.Release();
        await restores.ConfigureAwait(false);
    }

    /// <summary>
    /// Restores the exact map before its readiness gate publishes it to entering players.
    /// </summary>
    /// <param name="map">The initializing map.</param>
    /// <returns>The restore operation.</returns>
    public async ValueTask RestoreMapAsync(GameMap map)
    {
        lock (this._restoreLifecycle)
        {
            if (!this._active || map.HostServer?.GetId() != this._host.GetId())
            {
                return;
            }

            if (this._restoringMaps++ == 0)
            {
                this._restoresIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        try
        {
            await this.RestoreMapCoreAsync(map).ConfigureAwait(false);
        }
        finally
        {
            lock (this._restoreLifecycle)
            {
                if (--this._restoringMaps == 0)
                {
                    this._restoresIdle!.TrySetResult();
                }
            }
        }
    }

    /// <summary>
    /// Scans at most one live and one expired page; overlapping periodic ticks are skipped.
    /// Failed publications or deletions retain their durable generations for a later cycle.
    /// </summary>
    /// <returns>The scan operation.</returns>
    public async ValueTask ScanAsync()
    {
        if (!this._active || !await this._scanGate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (!this._active)
            {
                return;
            }

            await this.ScanPageAsync(true).ConfigureAwait(false);
            if (this._active)
            {
                await this.ScanPageAsync(false).ConfigureAwait(false);
            }
        }
        finally
        {
            this._scanGate.Release();
        }
    }

    private static DeliveryCursor CursorOf(Item item)
    {
        var delivery = item.GroundDelivery ?? throw new InvalidOperationException("A recovery row has no delivery generation.");
        return new DeliveryCursor(delivery.ExpiresAt, delivery.GetId());
    }

    private async ValueTask RestoreMapCoreAsync(GameMap map)
    {
        using var reader = this._gameContext.PersistenceContextProvider.CreateNewPlayerContext(this._gameContext.Configuration);
        DeliveryCursor? cursor = null;
        var deadline = DateTime.UtcNow;
        while (this._active && !map.IsTornDown)
        {
            var readStartedAt = Stopwatch.GetTimestamp();
            var page = await reader.GetGroundItemPageAsync(this.CreateQuery(false, deadline, cursor, map)).ConfigureAwait(false);
            foreach (var item in page)
            {
                if (!this._active)
                {
                    return;
                }

                await map.PublishGroundItemAsync(item, this._gameContext, readStartedAt: readStartedAt).ConfigureAwait(false);
            }

            if (page.Count < GroundItemQuery.MaximumPageSize)
            {
                return;
            }

            cursor = CursorOf(page[^1]);
        }
    }

    private GroundItemQuery CreateQuery(bool expired, DateTime now, DeliveryCursor? cursor, GameMap? map = null)
        => new()
        {
            HostServerId = this._host.GetId(),
            MapDefinitionId = map?.Definition.GetId(),
            MapInstanceId = map?.InstanceId,
            IncludeAllInstances = map is null,
            ExpiresAfterUtc = expired ? null : now,
            ExpiresBeforeUtc = expired ? now : null,
            AfterExpiresAtUtc = cursor?.ExpiresAt,
            AfterDeliveryId = cursor?.Id,
        };

    private async ValueTask ScanPageAsync(bool expired)
    {
        using var reader = this._gameContext.PersistenceContextProvider.CreateNewPlayerContext(this._gameContext.Configuration);
        var readStartedAt = Stopwatch.GetTimestamp();
        var page = await reader.GetGroundItemPageAsync(this.CreateQuery(expired, DateTime.UtcNow, expired ? this._expiredCursor : this._liveCursor)).ConfigureAwait(false);
        var next = page.Count == GroundItemQuery.MaximumPageSize ? CursorOf(page[^1]) : (DeliveryCursor?)null;
        foreach (var item in page)
        {
            if (!this._active)
            {
                return;
            }

            try
            {
                if (expired)
                {
                    await this.DeleteExpiredAsync(item).ConfigureAwait(false);
                }
                else if (await this.ResolveMapAsync(item.GroundDelivery!).ConfigureAwait(false) is { } map && this._active)
                {
                    await map.PublishGroundItemAsync(item, this._gameContext, readStartedAt: readStartedAt).ConfigureAwait(false);
                }
            }
            catch (PersistenceConflictException exception)
            {
                // Another context may have consumed the original generation after
                // the reader's statement snapshot. Never refresh it and try to win.
                this._logger.LogDebug(exception, "A ground delivery generation was consumed by another context.");
            }
            catch (Exception exception)
            {
                this._logger.LogWarning(exception, "Couldn't recover or expire ground item {Item}; a later scan will retry.", item.GetId());
            }
        }

        if (expired)
        {
            this._expiredCursor = next;
        }
        else
        {
            this._liveCursor = next;
        }
    }

    private async ValueTask<GameMap?> ResolveMapAsync(GroundItemDelivery delivery)
    {
        if (delivery.MapInstanceId is { } instance)
        {
            // An ended or pre-restart instance is never recreated as an ordinary
            // map. Its outstanding rows remain subject to the original expiry scan.
            return (await this._gameContext.GetMapsAsync().ConfigureAwait(false)).FirstOrDefault(map => !map.IsTornDown
                && map.InstanceId == instance && map.Definition.GetId() == delivery.MapDefinitionId
                && map.HostServer?.GetId() == this._host.GetId());
        }

        var definition = this._host.ServerConfiguration?.Maps.FirstOrDefault(map => map.GetId() == delivery.MapDefinitionId && map.IsActive);
        return definition is null ? null : await this._gameContext.GetMapAsync(definition.Number.ToUnsigned()).ConfigureAwait(false);
    }

    private async ValueTask DeleteExpiredAsync(Item item)
    {
        using var context = this._gameContext.PersistenceContextProvider.CreateNewTradeContext();

        // The reader supplied the complete detached aggregate, including original
        // token M, options and marker. One commit deletes the entire aggregate.
        context.Attach(item);
        if (await context.DeleteAsync(item).ConfigureAwait(false) && await context.SaveChangesAsync().ConfigureAwait(false))
        {
            this._gameContext.PlugInManager.GetPlugInPoint<IItemDestroyedPlugIn>()?.ItemDestroyed(item);
        }
    }

    private readonly record struct DeliveryCursor(DateTime ExpiresAt, Guid Id);
}
