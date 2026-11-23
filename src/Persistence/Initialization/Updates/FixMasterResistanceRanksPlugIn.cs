// <copyright file="FixMasterResistanceRanksPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.Persistence.Initialization.Skills;
using MUnique.OpenMU.PlugIns;

/// <summary>Aligns resistance ranks, prerequisites and the glove mastery level cap in the Season 6 master tree.</summary>
[PlugIn]
[Guid("31905ce4-6193-4e70-9eca-c294965577eb")]
[Display(Name = nameof(UpdateName), Description = nameof(UpdateDescription), ResourceType = typeof(FixMasterResistanceRanksPlugIn))]
public class FixMasterResistanceRanksPlugIn : UpdatePlugInBase
{
    /// <summary>Gets the localized update name.</summary>
    public static string UpdateName => PlugInResources.ResourceManager.GetString("FixMasterResistanceRanksPlugIn_Name")!;

    /// <summary>Gets the localized update description.</summary>
    public static string UpdateDescription => PlugInResources.ResourceManager.GetString("FixMasterResistanceRanksPlugIn_Description")!;

    /// <inheritdoc/>
    public override string Name => UpdateName;

    /// <inheritdoc/>
    public override string Description => UpdateDescription;

    /// <inheritdoc/>
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc/>
    public override bool IsMandatory => false;

    /// <inheritdoc/>
    public override DateTime CreatedAt => new(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc/>
    public override int Version => 2;

    /// <inheritdoc/>
    public override DateTime UpdatedAt => new(2026, 11, 23, 14, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc/>
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        foreach (var (number, rank) in new[] { (SkillNumber.LightningResistanceInc, (byte)3), (SkillNumber.IceResistanceIncrease, (byte)4) })
        {
            if (gameConfiguration.Skills.FirstOrDefault(s => s.Number == (short)number)?.MasterDefinition is { Rank: 2 } definition)
            {
                definition.Rank = rank;
            }
        }

        var skills = gameConfiguration.Skills.ToDictionary(s => (SkillNumber)s.Number);
        foreach (var number in new[] { SkillNumber.SummonedMonsterStr1, SkillNumber.SummonedMonsterStr2 })
        {
            if (skills.TryGetValue(number, out var skill) && skills.TryGetValue(SkillNumber.SummonGoblin, out var goblin))
            {
                skill.MasterDefinition?.RequiredMasterSkills.Remove(goblin);
            }
        }

        foreach (var (number, parent) in new[]
        {
            (SkillNumber.SummonedMonsterStr2, SkillNumber.SummonedMonsterStr1),
            (SkillNumber.CommandAttackInc, SkillNumber.UseScepterPetStr),
            (SkillNumber.KillingBlowStrengthener, SkillNumber.KillingBlow),
            (SkillNumber.BeastUppercutStrengthener, SkillNumber.BeastUppercut),
        })
        {
            if (skills.TryGetValue(number, out var skill) && skill.MasterDefinition is { } definition
                && skills.TryGetValue(parent, out var required) && !definition.RequiredMasterSkills.Contains(required))
            {
                definition.RequiredMasterSkills.Add(required);
            }
        }

        if (skills.TryGetValue(SkillNumber.EquippedWeaponMastery, out var mastery) && mastery.MasterDefinition is { MaximumLevel: 20 } gloveMastery)
        {
            gloveMastery.MaximumLevel = 10;
        }

        return ValueTask.CompletedTask;
    }
}
