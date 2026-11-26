// <copyright file="GameMap.GroundItems.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic;

using System.Collections.Concurrent;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.Persistence;
using Nito.AsyncEx;

/// <summary>
/// Stages and publishes durable ground deliveries at their actual host address.
/// </summary>
public partial class GameMap
{
    private readonly AsyncLock _groundPublicationLock = new();

    // Retain consumed generations until their original expiry: a scan may have read
    // an old statement snapshot just before pickup committed and removed the drop.
    private readonly ConcurrentDictionary<Guid, GroundPublication> _publishedGroundGenerations = new();

    /// <summary>
    /// Stages a delivery in the same context as its source action, without publishing it.
    /// </summary>
    /// <param name="item">The tracked item aggregate.</param>
    /// <param name="context">The context committing the action.</param>
    /// <param name="owner">The original owning character.</param>
    /// <param name="position">The original drop position.</param>
    /// <returns>The new delivery generation.</returns>
    internal GroundItemDelivery StageGroundDelivery(Item item, IContext context, Guid owner, Point position)
    {
        var host = this.HostServer?.GetId() ?? Guid.Empty;
        if (host == Guid.Empty || this.Definition.GetId() == Guid.Empty || owner == Guid.Empty || this.IsTornDown)
        {
            throw new InvalidOperationException("A durable ground delivery requires a live map with an actual host and owner.");
        }

        if (item.GroundDelivery is not null)
        {
            throw new InvalidOperationException("Consume the existing delivery before staging a new generation.");
        }

        var delivery = context.CreateNew<GroundItemDelivery>();
        delivery.HostServerId = host;
        delivery.MapDefinitionId = this.Definition.GetId();
        delivery.MapInstanceId = this.InstanceId;
        delivery.OwnerCharacterId = owner;
        delivery.PositionX = position.X;
        delivery.PositionY = position.Y;
        delivery.DroppedAt = DateTime.UtcNow;
        delivery.OwnerPriorityUntil = delivery.DroppedAt.Add(DroppedItem.OwnerPickupPriorityDuration);
        delivery.ExpiresAt = delivery.DroppedAt.Add(this.ItemDropDuration);
        item.GroundDelivery = delivery;
        return delivery;
    }

    /// <summary>
    /// Publishes a confirmed, detached delivery once, whether from its action or a recovery scan.
    /// </summary>
    /// <param name="item">The complete detached aggregate with its original generation.</param>
    /// <param name="gameContext">The persistence host, also available when the owner is offline.</param>
    /// <param name="dropper">The live original player, if any.</param>
    /// <param name="readStartedAt">The monotonic instant before the recovery query started; zero for action publication.</param>
    /// <returns>Whether this call newly published the generation.</returns>
    internal async ValueTask<bool> PublishGroundItemAsync(Item item, IGameContext gameContext, Player? dropper = null, long readStartedAt = 0)
    {
        var delivery = item.GroundDelivery ?? throw new InvalidOperationException("The item has no durable delivery.");
        if (delivery.GetId() == Guid.Empty || delivery.HostServerId != this.HostServer?.GetId()
            || delivery.MapDefinitionId != this.Definition.GetId() || delivery.MapInstanceId != this.InstanceId)
        {
            throw new InvalidOperationException("A durable delivery cannot be published on a different host, map or instance.");
        }

        using var publication = await this._groundPublicationLock.LockAsync().ConfigureAwait(false);
        var now = DateTime.UtcNow;
        foreach (var expired in this._publishedGroundGenerations.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToList())
        {
            this._publishedGroundGenerations.TryRemove(expired, out _);
        }

        if (this.IsTornDown || delivery.ExpiresAt <= now)
        {
            return false;
        }

        var generation = delivery.GetId();
        if (this._publishedGroundGenerations.TryGetValue(generation, out var previous))
        {
            var releasedAt = previous.UnconfirmedPlayer?.PersistenceContextReleasedAt ?? 0;
            if (releasedAt == 0 || readStartedAt <= releasedAt)
            {
                // A successful consumption stays terminal. After an uncertain pickup,
                // only a fresh read after the old tracker was released can establish
                // that M still exists. A delayed pre-release snapshot cannot do so.
                return false;
            }
        }

        var publishing = new GroundPublication(delivery.ExpiresAt, null, false);
        this._publishedGroundGenerations[generation] = publishing;
        var drop = new DroppedItem(item, this, gameContext, dropper);
        try
        {
            await this.AddAsync(drop).ConfigureAwait(false);
            if (this.IsTornDown)
            {
                await drop.DisposeAsync().ConfigureAwait(false);

                // Teardown may already have disposed it before AddAsync's observers
                // completed. Remove any late area-of-interest registration as well.
                await this.RemoveAsync(drop).ConfigureAwait(false);
                return false;
            }

            drop.StartExpiry();
            return true;
        }
        catch
        {
            try
            {
                try
                {
                    await drop.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    await this.RemoveAsync(drop).ConfigureAwait(false);
                }
            }
            catch (Exception cleanupError)
            {
                gameContext.LoggerFactory.CreateLogger<GameMap>().LogError(cleanupError, "Couldn't remove a failed ground publication {Generation}.", generation);
            }
            finally
            {
                if (!ReferenceEquals(this.GetDrop(drop.Id), drop))
                {
                    // An inline observer may already have consumed or quarantined
                    // the generation. Cleanup may revoke only its untouched claim.
                    ((ICollection<KeyValuePair<Guid, GroundPublication>>)this._publishedGroundGenerations).Remove(new(generation, publishing));
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Quarantines an uncertain pickup until its old player context is released.
    /// </summary>
    /// <param name="generation">The original delivery generation.</param>
    /// <param name="player">The only context still allowed to save the uncertain outcome.</param>
    internal void QuarantineGroundPickup(Guid generation, Player player)
    {
        // Publication holds its lock while notifying AddAsync observers. An observer
        // may pick up this drop inline, so quarantine must not acquire that lock.
        while (this._publishedGroundGenerations.TryGetValue(generation, out var previous))
        {
            if (this._publishedGroundGenerations.TryUpdate(generation, previous with { UnconfirmedPlayer = player }, previous))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Keeps a confirmed consumption terminal even if a publication observer subsequently fails.
    /// </summary>
    /// <param name="generation">The original delivery generation.</param>
    internal void ConfirmGroundPickup(Guid generation)
    {
        while (this._publishedGroundGenerations.TryGetValue(generation, out var previous))
        {
            if (this._publishedGroundGenerations.TryUpdate(generation, previous with { Consumed = true, UnconfirmedPlayer = null }, previous))
            {
                return;
            }
        }
    }

    private sealed record GroundPublication(DateTime ExpiresAt, Player? UnconfirmedPlayer, bool Consumed);
}
