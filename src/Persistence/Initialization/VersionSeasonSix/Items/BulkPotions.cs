// <copyright file="BulkPotions.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.Items;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;

/// <summary>Configures normal consumable stacks and additional bulk offers at the potion girl.</summary>
internal static class BulkPotions
{
    /// <summary>The configured stack and purchase quantity supported by the matching MuMain client.</summary>
    internal const byte StackSize = 100;

    private const byte PotionGroup = 14;
    private const byte FirstBulkSlot = 48;
    private const byte MerchantSlots = 120;

    /// <summary>Increases the capacity of the consumables sold by the potion girl.</summary>
    /// <param name="configuration">The Season 6 configuration.</param>
    internal static void ConfigureCapacity(GameConfiguration configuration)
    {
        foreach (var definition in configuration.Items.Where(IsSupported))
        {
            definition.Durability = Math.Max(definition.Durability, StackSize);
        }
    }

    /// <summary>Adds one bulk offer for each existing type and level without replacing smaller packs.</summary>
    /// <param name="context">The persistence context.</param>
    /// <param name="store">The potion girl's existing shop.</param>
    internal static void AddOffers(IContext context, ItemStorage store)
    {
        var templates = store.Items.Where(item => item.Definition is { } definition && IsSupported(definition))
            .GroupBy(item => (item.Definition, item.Level))
            .Select(group => group.First()).ToArray();
        foreach (var template in templates)
        {
            if (store.Items.Any(item => item.Definition == template.Definition && item.Level == template.Level && item.Durability == StackSize))
            {
                continue;
            }

            var slot = FindFreeSlot(store);
            if (slot is null)
            {
                continue;
            }

            var offer = context.CreateNew<Item>();
            offer.Definition = template.Definition;
            offer.Level = template.Level;
            offer.Durability = StackSize;
            offer.ItemSlot = slot.Value;
            store.Items.Add(offer);
        }
    }

    private static bool IsSupported(ItemDefinition definition)
        => definition.Group == PotionGroup && definition.Number is >= 0 and <= 6 or 8;

    private static byte? FindFreeSlot(ItemStorage store)
    {
        for (byte slot = FirstBulkSlot; slot < MerchantSlots; slot++)
        {
            var row = slot / InventoryConstants.RowSize;
            var column = slot % InventoryConstants.RowSize;
            if (!store.Items.Any(item => item.Definition is { } definition
                && row >= item.ItemSlot / InventoryConstants.RowSize
                && row < (item.ItemSlot / InventoryConstants.RowSize) + definition.Height
                && column >= item.ItemSlot % InventoryConstants.RowSize
                && column < (item.ItemSlot % InventoryConstants.RowSize) + definition.Width))
            {
                return slot;
            }
        }

        return null;
    }
}
