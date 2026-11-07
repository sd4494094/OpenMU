// <copyright file="AddLevelingWarpNpcPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Adds independently configured leveling destinations without enabling the optional service.
/// </summary>
[PlugIn]
[Guid("6a78a0b0-06cb-41d7-bfa2-f5ad83e20f8d")]
[Display(Name = nameof(Properties.PlugInResources.AddLevelingWarp_Name), Description = nameof(Properties.PlugInResources.AddLevelingWarp_Description), ResourceType = typeof(Properties.PlugInResources))]
public class AddLevelingWarpNpcPlugIn : UpdatePlugInBase
{
    /// <inheritdoc/>
    public override string Name => Properties.PlugInResources.AddLevelingWarp_Name;

    /// <inheritdoc/>
    public override string Description => Properties.PlugInResources.AddLevelingWarp_Description;

    /// <inheritdoc/>
    public override string DataInitializationKey => DataInitialization.Id;

    /// <inheritdoc/>
    public override bool IsMandatory => false;

    /// <inheritdoc/>
    public override DateTime CreatedAt => new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc/>
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        LevelingWarpInitialization.EnsureConfiguration(context, gameConfiguration);
        return ValueTask.CompletedTask;
    }
}
