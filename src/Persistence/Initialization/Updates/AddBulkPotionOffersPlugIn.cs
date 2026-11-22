// <copyright file="AddBulkPotionOffersPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.Items;
using MUnique.OpenMU.PlugIns;

/// <summary>Adds 100-piece potion offers to existing Season 6 installations.</summary>
[PlugIn]
[Guid("752bce80-5598-466a-b62d-fcad7582eee6")]
[Display(Name = nameof(PlugInResources.AddBulkPotionOffersPlugIn_Name), Description = nameof(PlugInResources.AddBulkPotionOffersPlugIn_Description), ResourceType = typeof(PlugInResources))]
public class AddBulkPotionOffersPlugIn : UpdatePlugInBase
{
    private const short PotionGirlNumber = 253;

    /// <inheritdoc />
    public override string Name => PlugInResources.AddBulkPotionOffersPlugIn_Name;

    /// <inheritdoc />
    public override string Description => PlugInResources.AddBulkPotionOffersPlugIn_Description;

    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        BulkPotions.ConfigureCapacity(gameConfiguration);
        if (gameConfiguration.Monsters.FirstOrDefault(monster => monster.Number == PotionGirlNumber)?.MerchantStore is { } store)
        {
            BulkPotions.AddOffers(context, store);
        }

        return ValueTask.CompletedTask;
    }
}
