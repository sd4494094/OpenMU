// <copyright file="StatAdvisorAttribute.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Resets;

/// <summary>A server-authorized base attribute in the advisor form.</summary>
/// <param name="Id">The stable strength, agility, vitality, energy, or leadership index.</param>
/// <param name="Name">The localized label.</param>
/// <param name="Current">The base value, excluding equipment and buffs.</param>
/// <param name="Maximum">The supported maximum base value.</param>
/// <param name="Amount">The proposed increase.</param>
public sealed record StatAdvisorAttribute(int Id, string Name, int Current, int Maximum, int Amount);
