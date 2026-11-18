// <copyright file="PlayerStorages.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic;

using MUnique.OpenMU.Persistence;

/// <summary>
/// The item storages of a <see cref="Player"/>.
/// </summary>
internal sealed class PlayerStorages
{
    private readonly Player _player;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayerStorages"/> class.
    /// </summary>
    /// <param name="player">The player.</param>
    public PlayerStorages(Player player)
    {
        this._player = player;
    }

    /// <summary>
    /// Gets the inventory of the selected character.
    /// </summary>
    public IInventoryStorage? Inventory { get; private set; }

    /// <summary>
    /// Gets or sets the temporary storage, which is used while an NPC or trade dialog is opened.
    /// </summary>
    public IStorage? TemporaryStorage { get; set; }

    /// <summary>
    /// Gets the shop storage of the selected character.
    /// </summary>
    public IShopStorage? ShopStorage { get; private set; }

    /// <summary>
    /// Gets or sets the vault. It's set when the vault NPC is opened.
    /// </summary>
    public IStorage? Vault { get; set; }

    /// <summary>
    /// Gets or sets the backup of the inventory, which is created before a trade and while the dialog of a crafting NPC is open.
    /// </summary>
    public BackupItemStorage? BackupInventory { get; set; }

    /// <summary>
    /// Creates the storages for the selected character, when it entered the world.
    /// </summary>
    /// <param name="character">The selected character.</param>
    public void CreateForCharacter(Character character)
    {
        this.Inventory = new InventoryStorage(this._player, this._player.GameContext);
        this.ShopStorage = new ShopStorage(character);
        this.TemporaryStorage = new Storage(InventoryConstants.TemporaryStorageSize, new TemporaryItemStorage());
        this.Vault = null; // vault storage is getting set when vault npc is opened.
    }

    /// <summary>
    /// Returns previously retained items when their rectangles fit in the current inventory.
    /// </summary>
    /// <returns>Whether any inventory items were added.</returns>
    public async ValueTask<bool> RecoverInventoryItemsAsync()
    {
        if (this.Inventory is not { } inventory
            || this._player.SelectedCharacter?.RecoveryStorage is not { } recovery)
        {
            return false;
        }

        var changed = false;
        foreach (var item in recovery.Items.ToList())
        {
            if (inventory.CheckInvSpace(item) is not { } slot)
            {
                continue;
            }

            var previousSlot = item.ItemSlot;
            recovery.Items.Remove(item);
            try
            {
                if (await inventory.AddItemAsync(slot, item).ConfigureAwait(false))
                {
                    changed = true;
                    continue;
                }
            }
            catch
            {
                // Adding may fail before or after updating the inventory collection.
                // Keep exactly one persistent owner even if a view callback throws.
                if (!inventory.ItemStorage.Items.Contains(item))
                {
                    item.ItemSlot = previousSlot;
                    recovery.Items.Add(item);
                }

                throw;
            }

            item.ItemSlot = previousSlot;
            recovery.Items.Add(item);
        }

        return changed;
    }

    /// <summary>
    /// Restores the temporary storage items placed in an NPC or trade dialog when player is disconnected.
    /// </summary>
    public async ValueTask RestoreTemporaryStorageItemsAsync()
    {
        try
        {
            if (this.Inventory is not { } inventory)
            {
                return;
            }

            if (this.BackupInventory is { } backupInventory)
            {
                await this.RestoreBackupInventoryAsync(inventory, backupInventory).ConfigureAwait(false);
                return;
            }

            if (this.TemporaryStorage is not { ItemStorage.Items.Count: > 0 } temporaryStorage)
            {
                // Nothing to restore.
                return;
            }

            var count = temporaryStorage.ItemStorage.Items.Count;
            this._player.Logger.LogInformation("Returning {count} items from temporary storage to inventory for player {player}", count, this._player.Name);

            if (await inventory.TryTakeAllAsync(temporaryStorage).ConfigureAwait(false))
            {
                this._player.Logger.LogInformation("Returned {count} items from temporary storage to inventory for player {player}", count, this._player.Name);
                this.TemporaryStorage = null;
                return;
            }

            // A successful mix can produce a larger item than its consumed inputs. The old
            // backup is no longer valid; retain the current results under the character
            // instead of discarding them or resurrecting the consumed ingredients.
            if (this._player.SelectedCharacter is not { } character)
            {
                throw new InvalidOperationException("Temporary items have no selected character owner.");
            }

            var recovery = character.RecoveryStorage ??= this._player.PersistenceContext.CreateNew<ItemStorage>();
            foreach (var item in temporaryStorage.Items.ToList())
            {
                await temporaryStorage.RemoveItemAsync(item).ConfigureAwait(false);
                if (!recovery.Items.Contains(item))
                {
                    recovery.Items.Add(item);
                }
            }

            this._player.Logger.LogInformation("Retained {count} items in recovery storage of player {player}", recovery.Items.Count, this._player.Name);
            this.TemporaryStorage = null;
        }
        catch (Exception ex)
        {
            this._player.Logger.LogError(ex, "Error returning items from temporary storage to inventory");
        }
    }

    private async ValueTask RestoreBackupInventoryAsync(IInventoryStorage inventory, BackupItemStorage backupInventory)
    {
        inventory.Clear();
        backupInventory.RestoreItemStates();
        foreach (var item in backupInventory.Items)
        {
            try
            {
                if (!await inventory.AddItemAsync(item.ItemSlot, item).ConfigureAwait(false)
                    && !await inventory.AddItemAsync(item).ConfigureAwait(false))
                {
                    this._player.Logger.LogError("Failed to restore item {item} from backup inventory of player {player}.", item, this._player.Name);
                }
            }
            catch (Exception ex)
            {
                this._player.Logger.LogError(ex, "Error restoring item {item} from backup inventory of player {player}.", item, this._player.Name);
            }
        }

        inventory.ItemStorage.Money = backupInventory.Money;
        this.BackupInventory = null;
        this.TemporaryStorage = null;
    }
}
