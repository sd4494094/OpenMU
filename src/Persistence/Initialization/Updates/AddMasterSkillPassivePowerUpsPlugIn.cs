// <copyright file="AddMasterSkillPassivePowerUpsPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.Persistence.Initialization.Skills;
using MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// This update adds the <see cref="MasterSkillDefinition.PassivePowerUps"/> of the master skills.
/// Before, the game logic applied the passive master skill values itself and knew some skills by their number.
/// </summary>
[PlugIn]
[Display(Name = PlugInName, Description = PlugInDescription)]
[Guid("684F5375-A5A6-41CC-83A6-479E33484707")]
public class AddMasterSkillPassivePowerUpsPlugIn : UpdatePlugInBase
{
    /// <summary>
    /// The plug in name.
    /// </summary>
    internal const string PlugInName = "Add passive power-ups of master skills";

    /// <summary>
    /// The plug in description.
    /// </summary>
    internal const string PlugInDescription = "Configures the passive effects of the master skills, which were hard-coded before. The new game logic requires them, otherwise passive master skills have no effect.";

    /// <inheritdoc />
    public override string Name => PlugInName;

    /// <inheritdoc />
    public override string Description => PlugInDescription;

    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override bool IsMandatory => true;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 10, 02, 19, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Gets the version of this update.
    /// </summary>
    /// <remarks>
    /// Version 2: castable master skills were missing their passive power-ups.
    /// Version 3: uses the reciprocal of the displayed divisor for the default kill recovery multipliers.
    /// Version 4: sends percentages for the default fractional and multiplier master skills.
    /// </remarks>
    public override int Version => 4;

    /// <inheritdoc />
    /// <remarks>
    /// Needs the target attribute which <see cref="FinishElfMasterTreePlugIn"/> sets.
    /// </remarks>
    public override DateTime UpdatedAt => new(2026, 11, 23, 6, 56, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override IEnumerable<UpdateDependency> DependsOn => [typeof(FinishElfMasterTreePlugIn).GUID];

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        if (gameConfiguration.Attributes.All(a => a.Id != Stats.MasterSkillValue.Id))
        {
            var attribute = context.CreateNew<AttributeDefinition>(Stats.MasterSkillValue.Id, Stats.MasterSkillValue.Designation, Stats.MasterSkillValue.Description);
            gameConfiguration.Attributes.Add(attribute);
        }

        MasterSkillPassivePowerUps.AddMissing(context, gameConfiguration);
        CorrectKillRecoveryMultipliers(gameConfiguration);
        CorrectPercentageDisplay(gameConfiguration);
        return ValueTask.CompletedTask;
    }

    private static void CorrectKillRecoveryMultipliers(GameConfiguration gameConfiguration)
    {
        foreach (var skill in gameConfiguration.Skills)
        {
            if (skill.MasterDefinition is not { TargetAttribute: { } target, Aggregation: AggregateType.AddFinal } definition
                || definition.ValueFormula != definition.DisplayValueFormula)
            {
                continue;
            }

            if (target.Id == Stats.ShieldAfterMonsterKillMultiplier.Id && definition.ValueFormula == SkillsInitializer.Formula914)
            {
                definition.ValueFormula = SkillsInitializer.KillRecoveryShieldMultiplier;
            }
            else if ((target.Id == Stats.HealthAfterMonsterKillMultiplier.Id || target.Id == Stats.ManaAfterMonsterKillMultiplier.Id)
                && definition.ValueFormula == SkillsInitializer.Formula4319)
            {
                definition.ValueFormula = SkillsInitializer.KillRecoveryHealthOrManaMultiplier;
            }
        }
    }

    private static void CorrectPercentageDisplay(GameConfiguration gameConfiguration)
    {
        foreach (var skill in gameConfiguration.Skills)
        {
            if (skill.MasterDefinition is not { } definition || definition.DisplayValueFormula != definition.ValueFormula)
            {
                continue;
            }

            var (legacy, display) = (SkillNumber)skill.Number switch
            {
                SkillNumber.DefenseSuccessRateInc => ($"1 + {SkillsInitializer.Formula120} / 100", SkillsInitializer.Formula120),
                SkillNumber.MaceMastery or SkillNumber.SpearMastery or SkillNumber.WindTomeMastery
                    => ($"{SkillsInitializer.Formula120} / 100", SkillsInitializer.Formula120),
                SkillNumber.SummonedMonsterStr1 or SkillNumber.SummonedMonsterStr2
                    => (SkillsInitializer.Formula6020Value, SkillsInitializer.Formula6020),
                SkillNumber.FireTomeMastery or SkillNumber.LightningTomeMastery
                    => ($"{SkillsInitializer.Formula181} / 100", SkillsInitializer.Formula181),
                _ => (null, null),
            };

            if (display is not null && definition.ValueFormula == legacy)
            {
                definition.DisplayValueFormula = display;
            }
        }
    }
}
