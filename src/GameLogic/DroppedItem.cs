// <copyright file="DroppedItem.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic;

using System.Threading;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.Persistence;
using Nito.AsyncEx;

/// <summary>
/// An item that got dropped on the ground of a map.
/// </summary>
public sealed class DroppedItem : AsyncDisposable, ILocateable
{
    private static readonly TimeSpan TimeUntilDropIsFree = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets the pickup lock. Used to synchronize pickup requests from the players.
    /// </summary>
    private readonly AsyncLock _pickupLock = new();

    private readonly DateTime _ownerPriorityUntil;

    private readonly DateTime _expiresAt;

    private readonly Guid? _ownerCharacterId;

    private readonly IGameContext? _persistenceHost;

    private readonly Guid? _deliveryGeneration;

    /// <summary>
    /// Indicates if the item was persistent (exists on the database) when it was dropped.
    /// If it wasn't and we clean it up, then we don't need to delete it.
    /// </summary>
    private readonly bool _wasItemPersisted;

    private Player? _dropper;

    private IEnumerable<object>? _owners;

    private Timer? _removeTimer;

    private volatile bool _availableToPick = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="DroppedItem" /> class.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="position">The position where the item was dropped on the map.</param>
    /// <param name="map">The map.</param>
    /// <param name="dropper">The dropper.</param>
    public DroppedItem(Item item, Point position, GameMap map, Player dropper)
        : this(item, position, map, dropper, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DroppedItem" /> class.
    /// </summary>
    /// <param name="item">The item, which should be detached from any player persistence context.</param>
    /// <param name="position">The position where the item was dropped on the map.</param>
    /// <param name="map">The map.</param>
    /// <param name="dropper">The dropper.</param>
    /// <param name="owners">The owners.</param>
    /// <param name="wasItemPersisted">If set to <c>true</c>, the item was persisted before and exists on the database.</param>
    public DroppedItem(Item item, Point position, GameMap map, Player? dropper, IEnumerable<object>? owners, bool wasItemPersisted = false)
        : this(item, position, map, dropper, owners, wasItemPersisted, dropper?.GameContext, false)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DroppedItem"/> class without starting its timer before map publication.
    /// </summary>
    /// <param name="item">The detached aggregate with its original delivery.</param>
    /// <param name="map">The exact destination map and instance.</param>
    /// <param name="host">The persistence host, also usable without an online owner.</param>
    /// <param name="dropper">The online original owner, if available.</param>
    internal DroppedItem(Item item, GameMap map, IGameContext host, Player? dropper)
        : this(item, new Point(item.GroundDelivery!.PositionX, item.GroundDelivery.PositionY), map, dropper, null, true, host, true)
    {
    }

    private DroppedItem(Item item, Point position, GameMap map, Player? dropper, IEnumerable<object>? owners, bool wasItemPersisted, IGameContext? host, bool deferExpiry)
    {
        this.Item = item;
        this.Position = position;
        this.CurrentMap = map;
        this._dropper = dropper;
        this._owners = owners;
        this._wasItemPersisted = wasItemPersisted;
        this._persistenceHost = host;
        var droppedAt = item.GroundDelivery?.DroppedAt ?? DateTime.UtcNow;
        this._ownerPriorityUntil = item.GroundDelivery?.OwnerPriorityUntil ?? droppedAt.Add(TimeUntilDropIsFree);
        this._expiresAt = item.GroundDelivery?.ExpiresAt ?? droppedAt.Add(map.ItemDropDuration);
        this._ownerCharacterId = item.GroundDelivery?.OwnerCharacterId;
        this._deliveryGeneration = item.GroundDelivery?.GetId();
        this._removeTimer = new Timer(this.DisposeAndDelete, null, Timeout.Infinite, Timeout.Infinite);
        if (!deferExpiry)
        {
            this.StartExpiry();
        }
    }

    /// <summary>
    /// Gets the item.
    /// </summary>
    public Item Item { get; }

    /// <inheritdoc />
    public Point Position { get; set; }

    /// <summary>
    /// Gets or sets the identifier.
    /// </summary>
    public ushort Id { get; set; }

    /// <inheritdoc/>
    public GameMap CurrentMap { get; }

    /// <summary>
    /// Gets a value indicating whether the owner-pickup priority period is still active.
    /// </summary>
    public bool IsOwnerPickupPriorityActive => DateTime.UtcNow < this._ownerPriorityUntil;

    /// <summary>
    /// Gets the original priority duration used when staging a durable drop.
    /// </summary>
    internal static TimeSpan OwnerPickupPriorityDuration => TimeUntilDropIsFree;

    /// <summary>
    /// Tries to pick the item up by the specified player.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns>
    /// The success.
    /// StackTarget: If the success is <c>true</c>, and this is not <c>null</c>, this dropped item was stacked on an existing item of the player inventory.
    /// </returns>
    /// <remarks>
    /// Can be overwritten, for example, for quest items which dropped only for a specific player.
    /// </remarks>
    public ValueTask<(bool Success, Item? StackTarget)> TryPickUpByAsync(Player player)
        => player.RunPersistenceExclusiveAsync(() => this.TryPickUpCoreAsync(player));

    /// <summary>
    /// Determines whether the specified player is an owner of this dropped item.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns><c>true</c> if the player is an owner; otherwise, <c>false</c>.</returns>
    public bool IsPlayerAnOwner(Player player)
    {
        if (this._ownerCharacterId is { } owner)
        {
            return player.SelectedCharacter?.GetId() == owner;
        }

        return this._owners?.Contains(player) ?? true;
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        return $"{this.Id}: {this.Item} at {this.CurrentMap.Definition.Name} ({this.Position})";
    }

    /// <summary>
    /// Starts the original remaining lifetime after a durable publication completes.
    /// </summary>
    internal void StartExpiry()
    {
        var remaining = this._expiresAt - DateTime.UtcNow;
        this._removeTimer?.Change(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    /// <inheritdoc/>
    protected override async ValueTask DisposeAsyncCore()
    {
        this._availableToPick = false;
        var timer = this._removeTimer;
        if (timer != null)
        {
            this._removeTimer = null;
            await timer.DisposeAsync().ConfigureAwait(false);
            await this.CurrentMap.RemoveAsync(this).ConfigureAwait(false);
            this._dropper = null;
            this._owners = null;
        }

        await base.DisposeAsyncCore().ConfigureAwait(false);
    }

    private async ValueTask<(bool Success, Item? StackTarget)> TryPickUpCoreAsync(Player player)
    {
        Item? stackTarget = null;
        if (!this._availableToPick
            || DateTime.UtcNow >= this._expiresAt
            || player.SelectedCharacter is null
            || player.PlayerState.CurrentState == PlayerState.Disconnected
            || player.PlayerState.CurrentState == PlayerState.Finished)
        {
            return (false, stackTarget);
        }

        if (!this.IsPlayerAnOwner(player)
            && (this.Item.Definition!.IsBoundToCharacter || this.IsOwnerPickupPriorityActive))
        {
            return (false, stackTarget);
        }

        if (this.Item.IsStackable())
        {
            stackTarget = player.Inventory?.Items.FirstOrDefault(i => i.CanCompletelyStackOn(this.Item));
        }

        if (stackTarget != null)
        {
            if (await this.TryStackOnItemAsync(player, stackTarget).ConfigureAwait(false))
            {
                return (true, stackTarget);
            }

            return (false, stackTarget);
        }

        return (await this.TryPickUpAsync(player).ConfigureAwait(false), stackTarget);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = "Catching all Exceptions.")]
    private async void DisposeAndDelete(object? state)
    {
        try
        {
            // Lock order: the player's persistence gate precedes this drop's pickup lock.
            // Expiry only takes the pickup lock and never enters a player's context.
            using var pickup = await this._pickupLock.LockAsync().ConfigureAwait(false);
            if (!this._availableToPick)
            {
                return;
            }

            this._availableToPick = false;
            await this.DisposeAsync().ConfigureAwait(false);
            if (this._persistenceHost is not null)
            {
                await this.DeleteItemAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // we have to catch all errors, because it runs under a pooled thread without an additional safety net ;-)
            this._persistenceHost?.LoggerFactory.CreateLogger<DroppedItem>().LogError(ex, "Error during DroppedItem.DisposeAndDeleteAsync");
        }
    }

    private async ValueTask<bool> TryPickUpAsync(Player player)
    {
        player.Logger.LogDebug("Player {0} tries to pick up {1}", player, this);
        var slot = player.Inventory?.CheckInvSpace(this.Item);
        if (!slot.HasValue || slot < InventoryConstants.LastEquippableItemSlotIndex)
        {
            player.Logger.LogDebug("Inventory full, Player {0}, Item {1}", player, this);
            return false;
        }

        var itemWasTemporary = this.Item is TemporaryItem;
        using (await this._pickupLock.LockAsync().ConfigureAwait(false))
        {
            if (!this._availableToPick || DateTime.UtcNow >= this._expiresAt)
            {
                player.Logger.LogDebug("Picked up by another player in the mean time, Player {0}, Item {1}", player, this);
                return false;
            }

            if (!itemWasTemporary)
            {
                // We already attach it here so that the next changes are in the context.
                player.PersistenceContext.Attach(this.Item);
            }

            if (!await player.Inventory!.AddItemAsync((byte)slot, this.Item).ConfigureAwait(false))
            {
                player.Logger.LogDebug("Item could not be added to the inventory, Player {0}, Item {1}", player, this);

                if (!itemWasTemporary)
                {
                    player.PersistenceContext.Detach(this.Item);
                }

                return false;
            }

            if (itemWasTemporary)
            {
                // We set the item slot in a temporary item manually, so the further logic can report to the client where the item has been added.
                // If it's temporary, it has been converted into a persistent item before it was added to the inventory. So it's different instance.
                // If it's not temporary, this step is not required because the inventory already set the ItemSlot in the same instance we're holding here.
                this.Item.ItemSlot = (byte)slot;
            }

            this._availableToPick = false;
            if (!itemWasTemporary && !await this.SavePickupOutcomeAsync(player).ConfigureAwait(false))
            {
                return false;
            }

            player.Logger.LogInformation("Item picked up: {Player} picked up '{DroppedItem}', dropped by {Dropper}.", player, this, this._dropper);
            await this.DisposeAsync().ConfigureAwait(false);
        }

        return true;
    }

    private async ValueTask<bool> TryStackOnItemAsync(Player player, Item stackTarget)
    {
        player.Logger.LogDebug("Player {0} tries to pick up {1}, trying to add to an existing item at slot {2}", player, this, stackTarget.ItemSlot);
        using (await this._pickupLock.LockAsync().ConfigureAwait(false))
        {
            if (!this._availableToPick || DateTime.UtcNow >= this._expiresAt)
            {
                player.Logger.LogDebug("Picked up by another player in the mean time, Player {0}, Item {1}", player, this);
                return false;
            }

            var itemWasTemporary = this.Item is TemporaryItem;
            if (!itemWasTemporary)
            {
                player.PersistenceContext.Attach(this.Item);
                try
                {
                    if (!await player.PersistenceContext.DeleteAsync(this.Item).ConfigureAwait(false))
                    {
                        player.PersistenceContext.Detach(this.Item);
                        return false;
                    }
                }
                catch (Exception exception)
                {
                    // No inventory delta or save has happened yet. Discard a partially
                    // staged deletion, retaining the unchanged source on the ground.
                    player.PersistenceContext.Detach(this.Item);
                    player.Logger.LogError(exception, "Couldn't stage consumption of '{DroppedItem}' for {Player}.", this, player);
                    return false;
                }
            }

            stackTarget.Durability += this.Item.Durability;
            this._availableToPick = false;
            if (!itemWasTemporary)
            {
                // Consume the source aggregate and update the tracked target in one commit.
                // A separate expiry/trade context could otherwise delete the source without
                // saving the new quantity, or save the quantity without consuming the source.
                if (!await this.SavePickupOutcomeAsync(player, stackTarget).ConfigureAwait(false))
                {
                    return false;
                }
            }

            await this.DisposeAsync().ConfigureAwait(false);
        }

        player.Logger.LogInformation("Item '{0}' got picked up by player '{1}'. Durability of available stack {2} increased to {3}", this, player, stackTarget, stackTarget.Durability);
        if (this._wasItemPersisted)
        {
            player.GameContext.PlugInManager.GetPlugInPoint<IItemDestroyedPlugIn>()?.ItemDestroyed(this.Item);
        }

        if (this.Item is TemporaryItem
            && player.GameContext.PlugInManager.GetPlugInPoint<IItemStackedPlugIn>() is { } itemStackedPlugIn)
        {
            await itemStackedPlugIn.ItemStackedAsync(player, this.Item, stackTarget).ConfigureAwait(false);
        }

        return true;
    }

    private async ValueTask<bool> SavePickupOutcomeAsync(Player player, Item? stackTarget = null)
    {
        try
        {
            if (stackTarget is null && this.Item.GroundDelivery is { } delivery)
            {
                // Attach has already captured generation M. Clearing it now and
                // deleting the marker must enter the same inventory commit.
                this.Item.GroundDelivery = null;
                if (!await player.PersistenceContext.DeleteAsync(delivery).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("Couldn't stage consumption of the ground delivery marker.");
                }
            }

            // Existing stacking hooks can turn a full stack into another item. Persist
            // their final inventory state with source consumption, before pickup success.
            if (stackTarget is not null
                && player.GameContext.PlugInManager.GetPlugInPoint<IItemStackedPlugIn>() is { } itemStackedPlugIn)
            {
                await itemStackedPlugIn.ItemStackedAsync(player, this.Item, stackTarget).ConfigureAwait(false);
            }

            if (await player.SaveProgressAsync().ConfigureAwait(false))
            {
                if (this._deliveryGeneration is { } generation)
                {
                    this.CurrentMap.ConfirmGroundPickup(generation);
                }

                return true;
            }

            player.Logger.LogError("The pickup of '{DroppedItem}' by {Player} was not saved; disconnecting.", this, player);
        }
        catch (Exception exception)
        {
            player.Logger.LogError(exception, "Couldn't confirm the pickup of '{DroppedItem}' by {Player}; disconnecting.", this, player);
        }

        // Retain the same tracked outcome for teardown's save. An unconfirmed commit may
        // already be durable; undoing it, detaching the source or adding the quantity again
        // could lose an item or permit a second grant. No pickup success escapes this session.
        try
        {
            await this.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            player.Logger.LogError(exception, "Couldn't remove the unconfirmed drop '{DroppedItem}'.", this);
        }
        finally
        {
            if (this._deliveryGeneration is { } generation)
            {
                this.CurrentMap.QuarantineGroundPickup(generation, player);
            }

            try
            {
                await player.DisconnectAsync().ConfigureAwait(false);
            }
            finally
            {
                if (this._deliveryGeneration is not null)
                {
                    // A base/test player has no remote subscriber which disposes it.
                    // Disposal publishes the release fence only after its tracker is gone.
                    await player.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        return false;
    }

    private async ValueTask DeleteItemAsync()
    {
        if (!this._wasItemPersisted || this._persistenceHost is not { } host)
        {
            return;
        }

        try
        {
            var repositoryManager = host.PersistenceContextProvider;

            // We could use here the persistence context of the dropper - but if it logged out and is not saving anymore, the deletion would not be saved.
            // So we use a new temporary persistence context instead.
            // We use a trade-context as it just focuses on the items. Otherwise, we would track a lot more items.
            using var context = repositoryManager.CreateNewTradeContext();
            context.Attach(this.Item);
            if (!await context.DeleteAsync(this.Item).ConfigureAwait(false)
                || !await context.SaveChangesAsync().ConfigureAwait(false))
            {
                return;
            }

            host.PlugInManager.GetPlugInPoint<IItemDestroyedPlugIn>()?.ItemDestroyed(this.Item);
        }
        catch (Exception e)
        {
            host.LoggerFactory.CreateLogger<DroppedItem>().LogWarning(e, "Couldn't delete expired item {Item}; durable delivery scanning can retry.", this.Item);
        }
    }
}
