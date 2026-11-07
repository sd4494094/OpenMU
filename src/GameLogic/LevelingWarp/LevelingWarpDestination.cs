// <copyright file="LevelingWarpDestination.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.LevelingWarp;

using MUnique.OpenMU.Interfaces;

/// <summary>
/// A stable destination identity with presentation and a server-owned landing gate.
/// </summary>
public class LevelingWarpDestination
{
    /// <summary>Gets or sets the stable wire number. Deleted numbers must not be reused.</summary>
    public int Number { get; set; }

    /// <summary>Gets or sets the full map and floor name.</summary>
    public LocalizedString Name { get; set; }

    /// <summary>Gets or sets the location description, such as a historical room name.</summary>
    public LocalizedString Description { get; set; }

    /// <summary>Gets or sets a value indicating whether this destination is enabled.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Gets or sets the base level, before the ordinary class reduction.</summary>
    public int LevelRequirement { get; set; }

    /// <summary>Gets or sets the price in Zen.</summary>
    public int Costs { get; set; }

    /// <summary>Gets or sets the server-owned landing gate.</summary>
    public ExitGate? Gate { get; set; }
}
