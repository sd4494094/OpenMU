// <copyright file="AddMuExCraftingMaterialsUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Adds the MuEx crafting material definitions to a Season 6 configuration.
/// </summary>
[PlugIn]
[Display(Name = UpdateName, Description = UpdateDescription)]
[Guid("87D9DA29-0903-47A1-A1DA-C86232C845FF")]
public sealed class AddMuExCraftingMaterialsUpdatePlugIn : UpdatePlugInBase
{
    private const string UpdateName = "MuEx crafting materials";
    private const string UpdateDescription = "Adds the 25 MuEx material and crafting note definitions for mixes 56 and 57. Existing definitions are preserved.";

    private static readonly (short Number, string NameKey, byte Height)[] Materials =
    [
        (176, "MuExSkeletonKingBone", 1),
        (177, "MuExForestSummonerHide", 1),
        (178, "MuExDarkPhoenixSpark", 1),
        (179, "MuExHellMaineSoul", 1),
        (180, "MuExIceWalkerMane", 1),
        (181, "MuExMammothFur", 1),
        (182, "MuExGiantIce", 2),
        (183, "MuExSpiderVenom", 1),
        (184, "MuExIronKnightHeart", 1),
        (185, "MuExDarkMammothHorn", 1),
        (186, "MuExDarkGiantAxe", 2),
        (187, "MuExDarkSpiderBlood", 1),
        (188, "MuExDarkIronKnightSword", 4),
        (191, "MuExGaleSwordNote", 1),
        (192, "MuExWhirlwindSwordNote", 1),
        (193, "MuExIceCrystalSwordNote", 1),
        (194, "MuExLavaSpearNote", 1),
        (195, "MuExDoomHammerNote", 1),
        (196, "MuExArchangelBowNote", 1),
        (197, "MuExDemonCrossbowNote", 1),
        (198, "MuExReincarnationStaffNote", 1),
        (199, "MuExResentmentStaffNote", 1),
        (200, "MuExAsuraSwordNote", 1),
        (201, "MuExThunderScepterNote", 1),
        (202, "MuExHolySpiritStaffNote", 1),
    ];

    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override string Name => UpdateName;

    /// <inheritdoc />
    public override string Description => UpdateDescription;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    public override bool RunsOnNewDatabase => true;

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        const byte group = 14;
        var existingNumbers = gameConfiguration.Items.Where(item => item.Group == group).Select(item => item.Number).ToHashSet();
        foreach (var (number, nameKey, height) in Materials)
        {
            if (existingNumbers.Contains(number))
            {
                continue;
            }

            var item = context.CreateNew<ItemDefinition>();
            item.Group = group;
            item.Number = number;
            item.Name = ItemNames.ResourceManager.GetLocalizedString(nameKey);
            item.Width = 1;
            item.Height = height;

            // Each definition is usable as a single inventory item while the original durability rules are unknown.
            item.Durability = 1;
            item.DropsFromMonsters = false;
            item.SetGuid(group, number);
            gameConfiguration.Items.Add(item);
        }

        return ValueTask.CompletedTask;
    }
}
