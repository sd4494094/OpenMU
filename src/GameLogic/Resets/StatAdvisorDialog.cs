// <copyright file="StatAdvisorDialog.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Resets;

/// <summary>The authoritative state of one stat advisor page.</summary>
/// <param name="SessionId">The identity of the current NPC conversation.</param>
/// <param name="Token">The one-use confirmation, empty outside a preview.</param>
/// <param name="Page">Home=0, reset preview=1, allocation=2, allocation preview=3, closed=4.</param>
/// <param name="Title">The localized NPC name.</param>
/// <param name="Message">The localized instructions or result.</param>
/// <param name="CanConfirm">Whether the current preview is executable.</param>
/// <param name="AvailablePoints">Unspent normal stat points, never master skill points.</param>
/// <param name="Stats">Attributes supported by the current class.</param>
/// <param name="Labels">Localized presentation labels.</param>
/// <param name="ClearInputs">Whether the client must discard any local draft amounts.</param>
public sealed record StatAdvisorDialog(string SessionId, string Token, int Page, string Title, string Message,
    bool CanConfirm, int AvailablePoints, IReadOnlyList<StatAdvisorAttribute> Stats, IReadOnlyDictionary<string, string> Labels, bool ClearInputs);
