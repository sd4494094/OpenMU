// <copyright file="EquipInventoryItemAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.Items;

using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.GameLogic.Views.Inventory;

/// <summary>
/// Changes the state marker of an item which stays in the inventory.
/// </summary>
public class EquipInventoryItemAction
{
    private const byte Active = 254;
    private const byte Inactive = 255;

    /// <summary>
    /// Stores and acknowledges the state of an eligible item in the player's inventory.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="slot">The inventory slot.</param>
    /// <param name="requestedState">The requested marker.</param>
    /// <remarks>
    /// MuMain carries the marker in the item's durability byte. The request only accepts items
    /// which already carry one of the two markers and are explicitly configured for this purpose.
    /// The packet dispatcher serializes the change and save against other player actions.
    /// </remarks>
    public async ValueTask EquipAsync(Player player, byte slot, byte requestedState)
    {
        if (requestedState is not (Active or Inactive)
            || player.PlayerState.CurrentState != PlayerState.EnteredWorld
            || slot < InventoryConstants.EquippableSlotsCount
            || slot >= InventoryConstants.FirstExtensionItemSlotIndex
            || player.Inventory?.GetItem(slot) is not { Definition: { } definition } item
            || item.ItemSlot != slot
            || item.Durability is not (Active or Inactive)
            || player.GameContext.FeaturePlugIns.GetPlugIn<InventoryItemEquipFeaturePlugIn>()?.Configuration is not { } allowedItems
            || !allowedItems.TryGetValue(definition.Group, out var numbers)
            || numbers?.Contains(definition.Number) != true)
        {
            return;
        }

        var previousState = item.Durability;
        if (previousState != requestedState)
        {
            var saved = false;
            item.Durability = requestedState;
            try
            {
                saved = await player.SaveProgressAsync().ConfigureAwait(false);
            }
            finally
            {
                if (!saved)
                {
                    item.Durability = previousState;
                }
            }
        }

        await player.InvokeViewPlugInAsync<IInventoryItemEquipResultPlugIn>(
            view => view.ShowInventoryItemEquipResultAsync(slot, (byte)item.Durability)).ConfigureAwait(false);
    }
}
