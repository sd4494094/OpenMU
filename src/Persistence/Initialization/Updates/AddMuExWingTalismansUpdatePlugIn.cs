// <copyright file="AddMuExWingTalismansUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Adds MuEx wing and cape talisman inventory definitions to a Season 6 configuration.
/// </summary>
[PlugIn]
[Display(Name = nameof(PlugInResources.AddMuExWingTalismans_Name), Description = nameof(PlugInResources.AddMuExWingTalismans_Description), ResourceType = typeof(PlugInResources))]
[Guid("109c4cc8-cf5b-421a-8bdc-c6f2cfcb024e")]
public sealed class AddMuExWingTalismansUpdatePlugIn : UpdatePlugInBase
{
    private const byte TalismanGroup = 13;

    private static readonly (short Number, string NameKey)[] Talismans =
    [
        (83, nameof(ItemNames.MuExTalismanOfWingsOfSatan)),
        (84, nameof(ItemNames.MuExTalismanOfWingsOfHeaven)),
        (85, nameof(ItemNames.MuExTalismanOfWingsOfElf)),
        (86, nameof(ItemNames.MuExTalismanOfWingOfCurse)),
        (87, nameof(ItemNames.MuExTalismanOfCapeOfEmperor)),
        (88, nameof(ItemNames.MuExTalismanOfWingsOfDragon)),
        (89, nameof(ItemNames.MuExTalismanOfWingsOfSoul)),
        (90, nameof(ItemNames.MuExTalismanOfWingsOfSpirits)),
        (91, nameof(ItemNames.MuExTalismanOfWingOfDespair)),
        (92, nameof(ItemNames.MuExTalismanOfWingsOfDarkness)),
    ];

    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override string Name => PlugInResources.AddMuExWingTalismans_Name;

    /// <inheritdoc />
    public override string Description => PlugInResources.AddMuExWingTalismans_Description;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    public override bool RunsOnNewDatabase => true;

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        var existingNumbers = gameConfiguration.Items.Where(item => item.Group == TalismanGroup)
            .Select(item => item.Number)
            .ToHashSet();
        foreach (var (number, nameKey) in Talismans)
        {
            if (existingNumbers.Contains(number))
            {
                continue;
            }

            var item = context.CreateNew<ItemDefinition>();
            item.Group = TalismanGroup;
            item.Number = number;
            item.Name = ItemNames.ResourceManager.GetLocalizedString(nameKey);
            item.Width = 1;
            item.Height = 1;

            // The client treats each talisman as one item. Use one durability for inventory until the original values are verified.
            item.Durability = 1;
            item.DropsFromMonsters = false;

            // Match the restrictions of the MuMain item catalog.
            item.IsDroppable = false;
            item.IsSellableToNpc = false;
            item.IsRepairable = false;
            item.SetGuid(TalismanGroup, number);
            gameConfiguration.Items.Add(item);
        }

        return ValueTask.CompletedTask;
    }
}
