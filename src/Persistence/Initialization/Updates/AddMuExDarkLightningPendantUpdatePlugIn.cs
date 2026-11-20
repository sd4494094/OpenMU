// <copyright file="AddMuExDarkLightningPendantUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Adds the separate MuEx dark lightning pendant definition to a Season 6 configuration.
/// </summary>
[PlugIn]
[Display(Name = nameof(PlugInResources.AddMuExDarkLightningPendant_Name), Description = nameof(PlugInResources.AddMuExDarkLightningPendant_Description), ResourceType = typeof(PlugInResources))]
[Guid("d66007cb-0be6-4feb-837f-93520ef05b4c")]
public sealed class AddMuExDarkLightningPendantUpdatePlugIn : UpdatePlugInBase
{
    private const byte PendantGroup = 13;
    private const short LightningPendantNumber = 12;
    private const short DarkLightningPendantNumber = 401;

    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override string Name => PlugInResources.AddMuExDarkLightningPendant_Name;

    /// <inheritdoc />
    public override string Description => PlugInResources.AddMuExDarkLightningPendant_Description;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    public override bool RunsOnNewDatabase => true;

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        if (gameConfiguration.Items.Any(item => item.Group == PendantGroup && item.Number == DarkLightningPendantNumber))
        {
            return ValueTask.CompletedTask;
        }

        var lightningPendant = gameConfiguration.Items.Single(item => item.Group == PendantGroup && item.Number == LightningPendantNumber);
        var darkPendant = context.CreateNew<ItemDefinition>();
        darkPendant.Group = PendantGroup;
        darkPendant.Number = DarkLightningPendantNumber;
        darkPendant.SetGuid(PendantGroup, DarkLightningPendantNumber);
        darkPendant.Name = LocalizedString.FromResource(() => ItemNames.MuExDarkLightningPendant);

        // The original base catalog differs only in identity and name. Keep the equipment properties from
        // this server's ordinary pendant. The original shop and ancient-set records require separate configuration.
        darkPendant.ItemSlot = lightningPendant.ItemSlot;
        darkPendant.StatProfile = lightningPendant.StatProfile;
        darkPendant.Width = lightningPendant.Width;
        darkPendant.Height = lightningPendant.Height;
        darkPendant.DropLevel = lightningPendant.DropLevel;
        darkPendant.MaximumItemLevel = lightningPendant.MaximumItemLevel;
        darkPendant.Durability = lightningPendant.Durability;
        darkPendant.Value = lightningPendant.Value;
        darkPendant.PriceDefinition = lightningPendant.PriceDefinition;
        darkPendant.DropsFromMonsters = false;

        foreach (var characterClass in lightningPendant.QualifiedCharacters)
        {
            darkPendant.QualifiedCharacters.Add(characterClass);
        }

        foreach (var option in lightningPendant.PossibleItemOptions)
        {
            darkPendant.PossibleItemOptions.Add(option);
        }

        foreach (var requirement in lightningPendant.Requirements)
        {
            var copy = context.CreateNew<AttributeRequirement>();
            copy.Attribute = requirement.Attribute;
            copy.MinimumValue = requirement.MinimumValue;
            copy.MinimumItemLevel = requirement.MinimumItemLevel;
            darkPendant.Requirements.Add(copy);
        }

        foreach (var powerUp in lightningPendant.BasePowerUpAttributes)
        {
            var copy = context.CreateNew<ItemBasePowerUpDefinition>();
            copy.TargetAttribute = powerUp.TargetAttribute;
            copy.BonusPerLevelTable = powerUp.BonusPerLevelTable;
            copy.BaseValue = powerUp.BaseValue;
            copy.AggregateType = powerUp.AggregateType;
            darkPendant.BasePowerUpAttributes.Add(copy);
        }

        gameConfiguration.Items.Add(darkPendant);
        return ValueTask.CompletedTask;
    }
}
