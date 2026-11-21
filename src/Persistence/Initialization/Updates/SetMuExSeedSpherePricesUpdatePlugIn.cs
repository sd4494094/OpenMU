// <copyright file="SetMuExSeedSpherePricesUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.Persistence.Initialization.Items;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Enables the MuEx client catalog's fixed seed sphere prices on a Season 6 configuration.
/// </summary>
[PlugIn]
[Display(Name = nameof(PlugInResources.SetMuExSeedSpherePrices_Name), Description = nameof(PlugInResources.SetMuExSeedSpherePrices_Description), ResourceType = typeof(PlugInResources))]
[Guid("a8249871-b8db-4835-a97a-4a4bd1821da4")]
public sealed class SetMuExSeedSpherePricesUpdatePlugIn : UpdatePlugInBase
{
    private const byte SeedSphereGroup = 12;
    private const short FirstSeedSphere = 100;
    private const int ElementsPerTier = 6;

    // MuEx Item.bmd gives each of the six elements the same fixed price within a tier.
    private static readonly int[] TierPrices = [1_050_000, 1_125_000, 1_200_000, 1_275_000, 1_350_000];

    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override string Name => PlugInResources.SetMuExSeedSpherePrices_Name;

    /// <inheritdoc />
    public override string Description => PlugInResources.SetMuExSeedSpherePrices_Description;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    public override bool RunsOnNewDatabase => true;

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        var end = FirstSeedSphere + (ElementsPerTier * TierPrices.Length);
        var spheres = gameConfiguration.Items.Where(item => item.Group == SeedSphereGroup && item.Number >= FirstSeedSphere && item.Number < end);
        var fixedPrice = new ItemPriceDefinitions(context, gameConfiguration).GetFixedValueDefinition();
        foreach (var item in spheres)
        {
            item.Value = TierPrices[(item.Number - FirstSeedSphere) / ElementsPerTier];
            item.PriceDefinition = fixedPrice;
        }

        return ValueTask.CompletedTask;
    }
}
