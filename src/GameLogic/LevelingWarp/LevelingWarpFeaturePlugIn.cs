// <copyright file="LevelingWarpFeaturePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.LevelingWarp;

using System.Runtime.InteropServices;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Owns the optional leveling NPC destination configuration.
/// </summary>
[PlugIn]
[Guid("93adb0b4-3464-4e23-8600-6b3d5f516d1a")]
[Display(Name = nameof(PlugInResources.LevelingWarp_Name), Description = nameof(PlugInResources.LevelingWarp_Description), ResourceType = typeof(PlugInResources))]
public class LevelingWarpFeaturePlugIn : IFeaturePlugIn, ISupportCustomConfiguration<LevelingWarpConfiguration>, ISupportDefaultCustomConfiguration, IDisabledByDefault
{
    /// <inheritdoc/>
    public LevelingWarpConfiguration? Configuration { get; set; }

    /// <inheritdoc/>
    public object CreateDefaultConfig() => new LevelingWarpConfiguration();
}
