// <copyright file="AddMuExBattlefieldSpoilsUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Adds the MuEx Battlefield Spoils item definition to a Season 6 configuration.
/// </summary>
[PlugIn]
[Display(Name = nameof(PlugInResources.AddMuExBattlefieldSpoils_Name), Description = nameof(PlugInResources.AddMuExBattlefieldSpoils_Description), ResourceType = typeof(PlugInResources))]
[Guid("3815e9a1-31c1-4a84-a764-f9c5c5b24f75")]
public sealed class AddMuExBattlefieldSpoilsUpdatePlugIn : UpdatePlugInBase
{
    private const byte SpoilsGroup = 13;
    private const short SpoilsNumber = 147;

    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override string Name => PlugInResources.AddMuExBattlefieldSpoils_Name;

    /// <inheritdoc />
    public override string Description => PlugInResources.AddMuExBattlefieldSpoils_Description;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    public override bool RunsOnNewDatabase => true;

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        if (gameConfiguration.Items.Any(item => item.Group == SpoilsGroup && item.Number == SpoilsNumber))
        {
            return ValueTask.CompletedTask;
        }

        var definition = context.CreateNew<ItemDefinition>();
        definition.Group = SpoilsGroup;
        definition.Number = SpoilsNumber;
        definition.Name = LocalizedString.FromResource(() => ItemNames.MuExBattlefieldSpoils);
        definition.Width = 1;
        definition.Height = 1;
        definition.Durability = 1;
        definition.DropsFromMonsters = false;
        definition.IsRepairable = false;
        definition.SetGuid(SpoilsGroup, SpoilsNumber);
        gameConfiguration.Items.Add(definition);
        return ValueTask.CompletedTask;
    }
}
