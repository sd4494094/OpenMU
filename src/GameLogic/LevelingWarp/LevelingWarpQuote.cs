// <copyright file="LevelingWarpQuote.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.LevelingWarp;

/// <summary>
/// An immutable display snapshot whose number, rather than text or row position, identifies the target.
/// </summary>
/// <param name="Number">The stable destination number.</param>
/// <param name="Name">The full localized map name.</param>
/// <param name="Description">The localized location description.</param>
/// <param name="Level">The current character's effective required level.</param>
/// <param name="Costs">The quoted price in Zen.</param>
/// <param name="Enabled">Whether the character currently meets the visible conditions.</param>
public sealed record LevelingWarpQuote(int Number, string Name, string Description, int Level, int Costs, bool Enabled);
