// <copyright file="ShowStatAdvisorExtendedPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.RemoteView.NPC;

using System.Runtime.InteropServices;
using System.Text.Json;
using MUnique.OpenMU.GameLogic.Resets;
using MUnique.OpenMU.GameLogic.Views.NPC;
using MUnique.OpenMU.Network.Packets.ServerToClient;
using MUnique.OpenMU.Network.PlugIns;
using MUnique.OpenMU.PlugIns;

/// <summary>Sends the stat advisor only after an extended client explicitly requests it.</summary>
[PlugIn]
[Guid("233c7c9b-049e-42ab-9abf-3edef0824bda")]
[Display(Name = nameof(PlugInResources.ShowStatAdvisor_Name), Description = nameof(PlugInResources.ShowStatAdvisor_Description), ResourceType = typeof(PlugInResources))]
[MinimumClient(106, 3, ClientLanguage.Invariant)]
public class ShowStatAdvisorExtendedPlugIn : IShowStatAdvisorPlugIn
{
    private readonly RemotePlayer _player;

    /// <summary>Initializes a new instance of the <see cref="ShowStatAdvisorExtendedPlugIn"/> class.</summary>
    /// <param name="player">The player.</param>
    public ShowStatAdvisorExtendedPlugIn(RemotePlayer player) => this._player = player;

    /// <inheritdoc/>
    public ValueTask ShowStatAdvisorAsync(StatAdvisorDialog dialog)
        => this._player.Connection.SendStatAdvisorDialogExtendedAsync(JsonSerializer.Serialize(dialog));
}
