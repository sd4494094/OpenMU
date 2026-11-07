// <copyright file="LevelingWarpConfiguration.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.LevelingWarp;

/// <summary>
/// Independently configured NPC destinations; never part of the ordinary warp list.
/// </summary>
public class LevelingWarpConfiguration
{
    /// <summary>Gets or sets the maximum NPC interaction distance, in tiles.</summary>
    public int MaximumDistance { get; set; } = 3;

    /// <summary>Gets or sets the independently enabled destinations.</summary>
    public List<LevelingWarpDestination> Destinations { get; set; } = [];
}
