// <copyright file="WeaponOffhandRestrictionPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns;

using System.ComponentModel;
using System.Runtime.InteropServices;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Restricts offhand equipping of configured weapons to the allowed character classes.
/// </summary>
[PlugIn]
[Display(Name = nameof(PlugInResources.WeaponOffhandRestrictionPlugIn_Name), Description = nameof(PlugInResources.WeaponOffhandRestrictionPlugIn_Description), ResourceType = typeof(PlugInResources))]
[Guid("ac22cb4e-f818-4e1f-9d1a-fe6e88acd0aa")]
public sealed class WeaponOffhandRestrictionPlugIn : IItemMovingPlugIn, ISupportCustomConfiguration<Dictionary<Guid, List<byte>>>, ISupportDefaultCustomConfiguration, IDisabledByDefault
{
    /// <summary>
    /// Gets or sets the map of item definition IDs to the classes allowed to equip them in the offhand.
    /// </summary>
    public Dictionary<Guid, List<byte>>? Configuration { get; set; }

    /// <inheritdoc />
    public object CreateDefaultConfig() => new Dictionary<Guid, List<byte>>();

    /// <inheritdoc />
    public void ItemMoving(Player player, Item item, Storages targetStorage, int slot, CancelEventArgs eventArgs)
    {
        if (targetStorage == Storages.Inventory && slot == InventoryConstants.RightHandSlot
            && item.Definition is IIdentifiable definition
            && this.Configuration?.TryGetValue(definition.Id, out var allowedClasses) == true
            && (player.SelectedCharacter?.CharacterClass is not { } characterClass
                || allowedClasses?.Contains(characterClass.Number) is not true))
        {
            eventArgs.Cancel = true;
        }
    }
}
