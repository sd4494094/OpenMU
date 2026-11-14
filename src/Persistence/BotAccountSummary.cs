// <copyright file="BotAccountSummary.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence;

/// <summary>A lightweight account projection; inventories, stats and credentials are never loaded.</summary>
/// <param name="LoginName">The internal account login.</param>
/// <param name="IsBot">Whether the account is explicitly marked as a bot.</param>
/// <param name="Characters">The persisted character names by slot.</param>
public sealed record BotAccountSummary(string LoginName, bool IsBot, IReadOnlyDictionary<byte, string> Characters);
