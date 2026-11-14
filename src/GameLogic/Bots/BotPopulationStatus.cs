// <copyright file="BotPopulationStatus.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>A process-local startup observation; it is not a promise that every bot stays online.</summary>
/// <param name="ServerId">The owning game server.</param>
/// <param name="Phase">The startup phase.</param>
/// <param name="SettingsMatch">Whether the generation settings match the requested plan.</param>
/// <param name="GenerationFailed">Whether this server's generation attempt failed.</param>
/// <param name="ManagedBots">The bots currently managed, including rotated offline bots.</param>
/// <param name="PendingRespawns">The slots awaiting a spawn retry.</param>
public sealed record BotPopulationStatus(byte? ServerId, string Phase, bool SettingsMatch, bool GenerationFailed, int ManagedBots, int PendingRespawns);
