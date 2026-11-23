// <copyright file="AddMasterSkillPassivePowerUpsPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.GameLogic.Attributes;
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
    /// </remarks>
    public override int Version => 3;

    /// <inheritdoc />
    /// <remarks>
    /// Needs the target attribute which <see cref="FinishElfMasterTreePlugIn"/> sets.
    /// </remarks>
    public override DateTime UpdatedAt => new(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc);

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
}
