// <copyright file="AddMuExMemoryResetItemsUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Adds the MuEx memory reset item definitions to a Season 6 configuration.
/// </summary>
[PlugIn]
[Display(Name = nameof(PlugInResources.AddMuExMemoryResetItems_Name), Description = nameof(PlugInResources.AddMuExMemoryResetItems_Description), ResourceType = typeof(PlugInResources))]
[Guid("93ae2450-03e7-4aae-9948-4ab6a9bc5c2b")]
public sealed class AddMuExMemoryResetItemsUpdatePlugIn : UpdatePlugInBase
{
    private const byte MemoryResetItemGroup = 13;
    private const short FirstSealedScrollNumber = 156;

    private static readonly (short Number, string NameKey, byte Width)[] Items =
    [
        (148, nameof(ItemNames.MuExGreenInk), 1),
        (149, nameof(ItemNames.MuExBlueInk), 1),
        (150, nameof(ItemNames.MuExRedInk), 1),
        (151, nameof(ItemNames.MuExSpiritParchment), 1),
        (152, nameof(ItemNames.MuExGreenForgetfulnessScroll), 2),
        (153, nameof(ItemNames.MuExBlueForgetfulnessScroll), 2),
        (154, nameof(ItemNames.MuExRedForgetfulnessScroll), 2),
        (155, nameof(ItemNames.MuExGrayForgetfulnessScroll), 2),
        (156, nameof(ItemNames.MuExSealedGreenForgetfulnessScroll), 2),
        (157, nameof(ItemNames.MuExSealedBlueForgetfulnessScroll), 2),
        (158, nameof(ItemNames.MuExSealedRedForgetfulnessScroll), 2),
        (159, nameof(ItemNames.MuExSealedGrayForgetfulnessScroll), 2),
    ];

    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override string Name => PlugInResources.AddMuExMemoryResetItems_Name;

    /// <inheritdoc />
    public override string Description => PlugInResources.AddMuExMemoryResetItems_Description;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    public override bool RunsOnNewDatabase => true;

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        var existingNumbers = gameConfiguration.Items.Where(item => item.Group == MemoryResetItemGroup)
            .Select(item => item.Number)
            .ToHashSet();
        foreach (var (number, nameKey, width) in Items)
        {
            if (existingNumbers.Contains(number))
            {
                continue;
            }

            var item = context.CreateNew<ItemDefinition>();
            item.Group = MemoryResetItemGroup;
            item.Number = number;
            item.Name = ItemNames.ResourceManager.GetLocalizedString(nameKey);
            item.Width = width;
            item.Height = 2;
            item.IsRepairable = false;
            item.IsTradable = number < FirstSealedScrollNumber;

            // Use one point of durability for inventory until the original item behavior is verified.
            item.Durability = 1;
            item.SetGuid(MemoryResetItemGroup, number);
            gameConfiguration.Items.Add(item);
        }

        return ValueTask.CompletedTask;
    }
}
