// <copyright file="ShowResetDialogExtendedPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.RemoteView.NPC;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.Views.NPC;
using MUnique.OpenMU.Network.Packets.ServerToClient;
using MUnique.OpenMU.Network.PlugIns;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Presents the reset menu on the open source client without changing the original protocol.
/// </summary>
[PlugIn]
[Guid("7d8c51e1-7490-4b40-bc7d-c4c9099df76a")]
[Display(Name = nameof(PlugInResources.ShowResetDialogExtendedPlugIn_Name), Description = nameof(PlugInResources.ShowResetDialogExtendedPlugIn_Description), ResourceType = typeof(PlugInResources))]
[MinimumClient(106, 3, ClientLanguage.Invariant)]
public class ShowResetDialogExtendedPlugIn : IShowResetDialogPlugIn
{
    private readonly RemotePlayer _player;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShowResetDialogExtendedPlugIn"/> class.
    /// </summary>
    /// <param name="player">The player.</param>
    public ShowResetDialogExtendedPlugIn(RemotePlayer player) => this._player = player;

    /// <inheritdoc />
    public ValueTask ShowResetDialogAsync(string confirmationToken, string title, string text, string queryLabel, string applyLabel, string closeLabel)
    {
        return this._player.Connection.SendResetNpcDialogExtendedAsync(confirmationToken, title, queryLabel, applyLabel, closeLabel, text);
    }
}
