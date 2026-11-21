// <copyright file="AddMuExElementalTalismansUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Adds inventory definitions for the MuEx elemental upgrade talismans.
/// </summary>
[PlugIn]
[Display(Name = nameof(PlugInResources.AddMuExElementalTalismans_Name), Description = nameof(PlugInResources.AddMuExElementalTalismans_Description), ResourceType = typeof(PlugInResources))]
[Guid("ac33dd52-b66e-4afc-a2f4-9791619e4f23")]
public sealed class AddMuExElementalTalismansUpdatePlugIn : UpdatePlugInBase
{
    private const byte TalismanGroup = 14;
    private const short LuckTalisman = 189;
    private const short ProtectionTalisman = 190;

    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override string Name => PlugInResources.AddMuExElementalTalismans_Name;

    /// <inheritdoc />
    public override string Description => PlugInResources.AddMuExElementalTalismans_Description;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    public override bool RunsOnNewDatabase => true;

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        AddMissing(LuckTalisman, nameof(ItemNames.MuExElementalTalismanOfLuck));
        AddMissing(ProtectionTalisman, nameof(ItemNames.MuExElementalTalismanOfChaosAssembly));
        return ValueTask.CompletedTask;

        void AddMissing(short number, string nameKey)
        {
            if (gameConfiguration.Items.Any(item => item.Group == TalismanGroup && item.Number == number))
            {
                return;
            }

            var definition = context.CreateNew<ItemDefinition>();
            definition.Group = TalismanGroup;
            definition.Number = number;
            definition.Name = ItemNames.ResourceManager.GetLocalizedString(nameKey);
            definition.Width = 1;
            definition.Height = 1;

            // Store one inventory unit until original elemental upgrade and quantity rules are verified.
            definition.Durability = 1;
            definition.DropsFromMonsters = false;
            definition.SetGuid(TalismanGroup, number);
            gameConfiguration.Items.Add(definition);
        }
    }
}
