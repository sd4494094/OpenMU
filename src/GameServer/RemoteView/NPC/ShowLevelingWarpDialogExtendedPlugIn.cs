// <copyright file="ShowLevelingWarpDialogExtendedPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.RemoteView.NPC;

using System.Runtime.InteropServices;
using System.Text.Json;
using MUnique.OpenMU.GameLogic.LevelingWarp;
using MUnique.OpenMU.GameLogic.Views.NPC;
using MUnique.OpenMU.Network.Packets.ServerToClient;
using MUnique.OpenMU.Network.PlugIns;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Sends a bounded, localized leveling menu without modifying the original move protocol.
/// </summary>
[PlugIn]
[Guid("21c68084-1bc0-4bfe-9114-10dc732e4ac1")]
[Display(Name = nameof(PlugInResources.ShowLevelingWarp_Name), Description = nameof(PlugInResources.ShowLevelingWarp_Description), ResourceType = typeof(PlugInResources))]
[MinimumClient(106, 3, ClientLanguage.Invariant)]
public class ShowLevelingWarpDialogExtendedPlugIn : IShowLevelingWarpDialogPlugIn
{
    private readonly RemotePlayer _player;

    /// <summary>Initializes a new instance of the <see cref="ShowLevelingWarpDialogExtendedPlugIn"/> class.</summary>
    /// <param name="player">The receiving player.</param>
    public ShowLevelingWarpDialogExtendedPlugIn(RemotePlayer player) => this._player = player;

    /// <inheritdoc/>
    public ValueTask ShowLevelingWarpDialogAsync(string token, string title, string closeLabel, string levelLabel, string costLabel, IReadOnlyList<LevelingWarpQuote> entries)
    {
        var json = JsonSerializer.Serialize(entries);
        return this._player.Connection.SendLevelingWarpDialogExtendedAsync(token, title, closeLabel, levelLabel, costLabel, json);
    }
}
