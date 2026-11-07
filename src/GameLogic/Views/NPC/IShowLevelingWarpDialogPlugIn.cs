// <copyright file="IShowLevelingWarpDialogPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Views.NPC;

using MUnique.OpenMU.GameLogic.LevelingWarp;

/// <summary>
/// Presents server-calculated destination quotes on a supported client.
/// </summary>
public interface IShowLevelingWarpDialogPlugIn : IViewPlugIn
{
    /// <summary>Shows one NPC session and its immutable display quotes.</summary>
    /// <param name="token">The one-use session token.</param>
    /// <param name="title">The localized title.</param>
    /// <param name="closeLabel">The localized close action.</param>
    /// <param name="levelLabel">The localized level label.</param>
    /// <param name="costLabel">The localized Zen label.</param>
    /// <param name="entries">The server-authoritative display rows.</param>
    ValueTask ShowLevelingWarpDialogAsync(string token, string title, string closeLabel, string levelLabel, string costLabel, IReadOnlyList<LevelingWarpQuote> entries);
}
