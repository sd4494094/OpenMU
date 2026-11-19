// <copyright file="SpiritMapFragmentStackedPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns;

using System.Runtime.InteropServices;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Converts a complete stack of Acheron entry fragments into a Spirit Map.
/// </summary>
[PlugIn]
[Display(Name = "Spirit Map fragment stacking", Description = "Converts a full stack of Spirit Map fragments into a Spirit Map.")]
[Guid("EE6A8BAD-2B8F-40F0-8F8E-32AFE3808334")]
public sealed class SpiritMapFragmentStackedPlugIn : IItemStackedPlugIn
{
    // These item keys match the MuEx client catalog.
    private const byte ItemGroup = 13;
    private const short FragmentNumber = 145;
    private const short MapNumber = 146;

    /// <inheritdoc />
    public async ValueTask ItemStackedAsync(Player player, Item sourceItem, Item targetItem)
    {
        if (targetItem.Definition is not { Group: ItemGroup, Number: FragmentNumber } fragment
            || targetItem.Durability != fragment.Durability)
        {
            return;
        }

        if (player.GameContext.Configuration.Items.FirstOrDefault(item => item is { Group: ItemGroup, Number: MapNumber }) is not { } map)
        {
            player.Logger.LogWarning("Spirit Map definition not found.");
            return;
        }

        await player.InvokeViewPlugInAsync<Views.Inventory.IItemRemovedPlugIn>(p => p.RemoveItemAsync(targetItem.ItemSlot)).ConfigureAwait(false);
        targetItem.Definition = map;
        targetItem.Durability = 1;
        await player.InvokeViewPlugInAsync<Views.Inventory.IItemAppearPlugIn>(p => p.ItemAppearAsync(targetItem)).ConfigureAwait(false);
    }
}
