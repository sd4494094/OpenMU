// <copyright file="EquipmentWeaponKinds.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel.Operations;

using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.GameLogic.Attributes;

/// <summary>Describes weapon categories using configured equipment markers and hand slots.</summary>
internal static class EquipmentWeaponKinds
{
    /// <summary>The protocol axe group; configuration has no separate axe equipment marker.</summary>
    internal const byte AxeGroup = 1;

    /// <summary>The supported search categories, including weapons without a recognized kind.</summary>
    internal static readonly string[] Types = ["All", "Sword", "Axe", "Mace", "Spear", "Bow", "Crossbow", "Staff", "Stick", "Book", "Scepter", "Glove", "Other"];

    /// <summary>Gets a weapon kind; shields and items without a configured weapon marker are excluded.</summary>
    /// <param name="item">The configured item.</param>
    /// <returns>A category, or null for non-weapons.</returns>
    internal static string? TypeOf(ItemDefinition item)
    {
        if (item.IsAmmunition || !Has(item, Stats.EquippedWeaponCount))
        {
            return null;
        }

        if (Has(item, Stats.IsGloveWeaponEquipped))
        {
            return "Glove";
        }

        if (Has(item, Stats.IsScepterEquipped))
        {
            return "Scepter";
        }

        if (Has(item, Stats.IsBookEquipped))
        {
            return "Book";
        }

        if (Has(item, Stats.IsStickEquipped))
        {
            return "Stick";
        }

        if (Has(item, Stats.IsBowEquipped))
        {
            return "Bow";
        }

        if (Has(item, Stats.IsCrossBowEquipped))
        {
            return "Crossbow";
        }

        if (Has(item, Stats.IsOneHandedStaffEquipped) || Has(item, Stats.IsTwoHandedStaffEquipped))
        {
            return "Staff";
        }

        if (Has(item, Stats.IsOneHandedSwordEquipped) || Has(item, Stats.IsTwoHandedSwordEquipped))
        {
            return "Sword";
        }

        if (Has(item, Stats.IsMaceEquipped))
        {
            return "Mace";
        }

        if (Has(item, Stats.IsSpearEquipped))
        {
            return "Spear";
        }

        return item.Group == AxeGroup ? "Axe" : "Other";
    }

    /// <summary>Gets hand usage from weapon markers and the slots/width used by the equip rules.</summary>
    /// <param name="item">The configured weapon.</param>
    /// <returns>OneHanded, TwoHanded, Ranged, or Unknown.</returns>
    internal static string HandednessOf(ItemDefinition item)
    {
        if (Has(item, Stats.IsBowEquipped) || Has(item, Stats.IsCrossBowEquipped))
        {
            return "Ranged";
        }

        if (Has(item, Stats.IsTwoHandedWeaponEquipped) || Has(item, Stats.IsTwoHandedSwordEquipped) || Has(item, Stats.IsTwoHandedStaffEquipped))
        {
            return "TwoHanded";
        }

        if (Has(item, Stats.IsOneHandedSwordEquipped) || Has(item, Stats.IsOneHandedStaffEquipped)
            || item.ItemSlot?.ItemSlots.Contains(InventoryConstants.RightHandSlot) == true)
        {
            return "OneHanded";
        }

        return item.ItemSlot?.ItemSlots.Contains(InventoryConstants.LeftHandSlot) == true
            ? item.Width >= 2 ? "TwoHanded" : "OneHanded"
            : "Unknown";
    }

    private static bool Has(ItemDefinition item, AttributeDefinition attribute)
        => item.BasePowerUpAttributes.Any(powerUp => powerUp.TargetAttribute == attribute && powerUp.BaseValue > 0);
}
