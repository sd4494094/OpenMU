// <copyright file="IShowPartyFullPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Views.Party;

using System.Runtime.InteropServices;

/// <summary>
/// Informs a player that an invitation could not be completed because the party is full.
/// </summary>
[Guid("65b9060f-b10b-4093-8cb7-a5e8553b5e44")]
public interface IShowPartyFullPlugIn : IViewPlugIn
{
    /// <summary>
    /// Shows the party capacity failure using the client's localized message.
    /// </summary>
    ValueTask ShowPartyFullAsync();
}
