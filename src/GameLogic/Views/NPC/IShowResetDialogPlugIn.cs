// <copyright file="IShowResetDialogPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Views.NPC;

/// <summary>
/// Shows the reset NPC menu and a server-calculated confirmation.
/// </summary>
public interface IShowResetDialogPlugIn : IViewPlugIn
{
    /// <summary>
    /// Shows the reset dialog, with no reset performed by displaying it.
    /// </summary>
    /// <param name="confirmationToken">A one-use confirmation token, or empty for the menu.</param>
    /// <param name="title">The localized NPC name.</param>
    /// <param name="text">The current reset requirements and consequences.</param>
    /// <param name="queryLabel">The query button label.</param>
    /// <param name="applyLabel">The apply or confirm button label.</param>
    /// <param name="closeLabel">The close button label.</param>
    ValueTask ShowResetDialogAsync(string confirmationToken, string title, string text, string queryLabel, string applyLabel, string closeLabel);
}
