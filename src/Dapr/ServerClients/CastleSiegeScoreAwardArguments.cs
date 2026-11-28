// <copyright file="CastleSiegeScoreAwardArguments.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.ServerClients;

/// <summary>
/// Identifies an idempotent Castle Siege guild score award.
/// </summary>
/// <param name="PersistentGuildId">The persistent identifier of the guild.</param>
/// <param name="BattleStartUtc">The UTC start time of the battle.</param>
/// <param name="Amount">The score amount to add.</param>
public record CastleSiegeScoreAwardArguments(Guid PersistentGuildId, DateTime BattleStartUtc, int Amount);
