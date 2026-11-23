// <copyright file="AddMuExInventoryEffectItemsUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Adds the item definitions for the MuEx figurines, charms and worn horseshoe.
/// </summary>
[PlugIn]
[Display(Name = nameof(PlugInResources.AddMuExInventoryEffectItems_Name), Description = nameof(PlugInResources.AddMuExInventoryEffectItems_Description), ResourceType = typeof(PlugInResources))]
[Guid("839a272f-67b7-4eb2-8a8a-b1e2f51df492")]
public sealed class AddMuExInventoryEffectItemsUpdatePlugIn : UpdatePlugInBase
{
    private const byte ItemGroup = 13;

    private static readonly (short Number, byte Height, string NameKey)[] Definitions =
    [
        (128, 1, nameof(ItemNames.MuExHawkFigurine)),
        (129, 1, nameof(ItemNames.MuExGoatFigurine)),
        (130, 1, nameof(ItemNames.MuExOakCharm)),
        (131, 1, nameof(ItemNames.MuExMapleCharm)),
        (132, 2, nameof(ItemNames.MuExGoldenOakCharm)),
        (133, 2, nameof(ItemNames.MuExGoldenMapleCharm)),
        (134, 1, nameof(ItemNames.MuExWornHorseshoe)),
    ];

    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override string Name => PlugInResources.AddMuExInventoryEffectItems_Name;

    /// <inheritdoc />
    public override string Description => PlugInResources.AddMuExInventoryEffectItems_Description;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 11, 23, 8, 20, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    public override bool RunsOnNewDatabase => true;

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        foreach (var (number, height, nameKey) in Definitions)
        {
            if (gameConfiguration.Items.Any(item => item.Group == ItemGroup && item.Number == number))
            {
                continue;
            }

            var definition = context.CreateNew<ItemDefinition>();
            definition.Group = ItemGroup;
            definition.Number = number;
            definition.Name = ItemNames.ResourceManager.GetLocalizedString(nameKey);
            definition.Width = 1;
            definition.Height = height;

            // The original item table starts these items at 255, the inactive inventory marker.
            definition.Durability = byte.MaxValue;
            definition.IsRepairable = false;
            definition.DropsFromMonsters = false;
            definition.SetGuid(ItemGroup, number);
            gameConfiguration.Items.Add(definition);
        }

        return ValueTask.CompletedTask;
    }
}
