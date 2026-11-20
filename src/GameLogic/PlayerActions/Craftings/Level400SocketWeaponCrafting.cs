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
    /// Initializes a new instance of the <see cref="Level400SocketWeaponCrafting"/> class.
    /// </summary>
    /// <param name="settings">The crafting settings.</param>
    public Level400SocketWeaponCrafting(SimpleCraftingSettings settings)
        : base(settings)
    {
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
