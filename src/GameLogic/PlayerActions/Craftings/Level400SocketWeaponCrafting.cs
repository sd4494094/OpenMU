// <copyright file="Level400SocketWeaponCrafting.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.Craftings;

using MUnique.OpenMU.DataModel.Configuration.ItemCrafting;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;

/// <summary>
/// Handles the materials of the level 400 socket weapon recipes.
/// </summary>
/// <remarks>
/// Configure <see cref="ItemOptionTypes.Option"/> on the equipment requirement.
/// The equipment must have sockets and at least one level of the ordinary additional option.
/// The recipe table expresses that threshold as 4 points, which corresponds to one option level
/// for equipment (4 points, or 5 for shield blocking).
/// Empty sockets count toward eligibility.
/// </remarks>
public class Level400SocketWeaponCrafting : SimpleItemCraftingHandler
{
    /// <summary>
    /// The price increase per socket, including empty sockets, used by the MuMain socket weapon preview.
    /// This compatibility calculation is scoped to this recipe handler.
    /// </summary>
    internal const float MuMainSocketPriceIncrease = 0.8f;

    /// <summary>
    /// Initializes a new instance of the <see cref="Level400SocketWeaponCrafting"/> class.
    /// </summary>
    /// <param name="settings">The crafting settings.</param>
    public Level400SocketWeaponCrafting(SimpleCraftingSettings settings)
        : base(settings)
    {
    }

    /// <inheritdoc />
    protected override long GetPriceBasedMaterialValue(Player player, Item item)
    {
        if (item.SocketCount > 0 && item.Definition is { MaximumSockets: > 0 } definition
            && definition.PriceDefinition?.CraftingReferencePrice is null)
        {
            var configuration = player.GameContext.FeaturePlugIns.GetPlugIn<SocketWeaponCraftingFeaturePlugIn>()?.Configuration;

            // Use the first option for each occupied slot, matching the item sent to the client.
            long mountedValue = 0;
            for (var slot = 0; slot < item.SocketCount; slot++)
            {
                var sphere = item.ItemOptions.FirstOrDefault(link => link.Index == slot && link.ItemOption?.OptionType == ItemOptionTypes.SocketOption);
                if (sphere is not null)
                {
                    mountedValue += SocketWeaponCraftingConfiguration.GetMountedSeedSphereValue(configuration, sphere.Level);
                }
            }

            return ItemPriceCalculator.CalculateAdjustedBuyingPrice(item, item.SocketCount * MuMainSocketPriceIncrease, mountedValue);
        }

        return base.GetPriceBasedMaterialValue(player, item);
    }

    /// <inheritdoc />
    protected override int ApplySuccessRateLimit(int baseRate, int fixedMaterialBonus)
    {
        // MuMain caps the rate calculated from the mix materials before adding the Talisman of Luck.
        return base.ApplySuccessRateLimit(baseRate, 0) + fixedMaterialBonus;
    }

    /// <inheritdoc />
    protected override bool RequiredItemMatches(Item item, ItemCraftingRequiredItem requiredItem)
    {
        if (!base.RequiredItemMatches(item, requiredItem))
        {
            return false;
        }

        if (!requiredItem.RequiredItemOptions.Any(option => option == ItemOptionTypes.Option))
        {
            return true;
        }

        return item.SocketCount > 0
               && item.ItemOptions.FirstOrDefault(option =>
                   option.ItemOption?.OptionType == ItemOptionTypes.Option && !option.ItemOption.IsFixedOption()) is { Level: > 0 };
    }
}
