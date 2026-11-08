// <copyright file="IShowStatAdvisorPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Views.NPC;

using MUnique.OpenMU.GameLogic.Resets;

/// <summary>Displays the explicitly negotiated stat advisor on a supporting client.</summary>
public interface IShowStatAdvisorPlugIn : IViewPlugIn
{
    /// <summary>Shows an authoritative menu, form, preview, or operation result.</summary>
    /// <param name="dialog">The localized dialog state.</param>
    ValueTask ShowStatAdvisorAsync(StatAdvisorDialog dialog);
}
