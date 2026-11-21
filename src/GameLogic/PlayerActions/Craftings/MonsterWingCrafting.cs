// <copyright file="MonsterWingCrafting.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.Craftings;

using MUnique.OpenMU.DataModel.Configuration.ItemCrafting;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;

/// <summary>
/// Calculates the monster wing mix rate from the value of the wing or cape alone.
/// </summary>
public class MonsterWingCrafting : SimpleItemCraftingHandler
{
    private readonly ItemPriceCalculator _priceCalculator = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="MonsterWingCrafting"/> class.
    /// </summary>
    /// <param name="settings">The crafting settings.</param>
    public MonsterWingCrafting(SimpleCraftingSettings settings)
        : base(settings)
    {
    }

    /// <inheritdoc />
    protected override long GetPriceBasedMaterialValue(Player player, Item item)
        => item.IsWing() ? this._priceCalculator.CalculateFinalBuyingPrice(item) : 0;

    /// <inheritdoc />
    protected override int ApplySuccessRateLimit(int baseRate, int fixedMaterialBonus)
        => base.ApplySuccessRateLimit(baseRate, 0) + fixedMaterialBonus;
}
