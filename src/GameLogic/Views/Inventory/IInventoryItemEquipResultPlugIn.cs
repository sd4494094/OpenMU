// <copyright file="IInventoryItemEquipResultPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Views.Inventory;

/// <summary>
/// Displays the confirmed state of an item which stays in the inventory.
/// </summary>
public interface IInventoryItemEquipResultPlugIn : IViewPlugIn
{
    /// <summary>
    /// Shows the current marker for the inventory slot.
    /// </summary>
    /// <param name="slot">The inventory slot.</param>
    /// <param name="state">The confirmed marker.</param>
    ValueTask ShowInventoryItemEquipResultAsync(byte slot, byte state);
}
