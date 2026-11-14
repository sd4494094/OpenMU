// <copyright file="BotAccountCharacterRow.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.EntityFramework;

/// <summary>A flat projection over the mapped account-character relationship.</summary>
/// <param name="LoginName">The internal account login.</param>
/// <param name="CharacterSlot">The persisted character slot.</param>
/// <param name="Name">The persisted character name.</param>
internal sealed record BotAccountCharacterRow(string LoginName, byte CharacterSlot, string Name);
