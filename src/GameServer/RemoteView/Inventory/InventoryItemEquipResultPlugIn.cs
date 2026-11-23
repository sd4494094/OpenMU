// <copyright file="InventoryItemEquipResultPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.RemoteView.Inventory;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.Views.Inventory;
using MUnique.OpenMU.Network.Packets.ServerToClient;
using MUnique.OpenMU.Network.PlugIns;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Sends the confirmed inventory item state to MuMain.
/// </summary>
[PlugIn]
[Display(Name = nameof(PlugInResources.InventoryItemEquipResult_Name), Description = nameof(PlugInResources.InventoryItemEquipResult_Description), ResourceType = typeof(PlugInResources))]
[Guid("b0a45e7f-4f50-47cf-9a4a-cbf25467485d")]
[MinimumClient(106, 3, ClientLanguage.Invariant)]
internal class InventoryItemEquipResultPlugIn : IInventoryItemEquipResultPlugIn
{
    private readonly RemotePlayer _player;

    /// <summary>
    /// Initializes a new instance of the <see cref="InventoryItemEquipResultPlugIn"/> class.
    /// </summary>
    /// <param name="player">The remote player.</param>
    public InventoryItemEquipResultPlugIn(RemotePlayer player) => this._player = player;

    /// <inheritdoc />
    public ValueTask ShowInventoryItemEquipResultAsync(byte slot, byte state) =>
        this._player.Connection.SendInventoryItemEquipResultAsync(slot, state);
}
