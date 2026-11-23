// <copyright file="FixMasterResistanceRanksPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.Persistence.Initialization.Skills;
using MUnique.OpenMU.PlugIns;

/// <summary>Corrects the ranks of the shared resistance skills in the Season 6 master tree.</summary>
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
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        foreach (var (number, rank) in new[] { (SkillNumber.LightningResistanceInc, (byte)3), (SkillNumber.IceResistanceIncrease, (byte)4) })
        {
            if (gameConfiguration.Skills.FirstOrDefault(s => s.Number == (short)number)?.MasterDefinition is { Rank: 2 } definition)
            {
                definition.Rank = rank;
            }
        }

        return ValueTask.CompletedTask;
    }
}
