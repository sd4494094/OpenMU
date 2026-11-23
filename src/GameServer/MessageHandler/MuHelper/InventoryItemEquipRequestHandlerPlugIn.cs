// <copyright file="InventoryItemEquipRequestHandlerPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MessageHandler.MuHelper;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.Network.Packets.ClientToServer;
using MUnique.OpenMU.Network.PlugIns;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Handles inventory item state requests from MuMain.
/// </summary>
[PlugIn]
[Display(Name = nameof(PlugInResources.InventoryItemEquipRequest_Name), Description = nameof(PlugInResources.InventoryItemEquipRequest_Description), ResourceType = typeof(PlugInResources))]
[Guid("53e247ef-3b6e-467d-b20e-c1f572f4797f")]
[MinimumClient(106, 3, ClientLanguage.Invariant)]
[BelongsToGroup(MuHelperGroupHandler.GroupKey)]
internal class InventoryItemEquipRequestHandlerPlugIn : ISubPacketHandlerPlugIn
{
    private readonly EquipInventoryItemAction _action = new();

    /// <inheritdoc />
    public bool IsEncryptionExpected => false;

    /// <inheritdoc />
    public byte Key => InventoryItemEquipRequest.SubCode;

    /// <inheritdoc />
    public ValueTask HandlePacketAsync(Player player, Memory<byte> packet)
    {
        if (packet.Length != InventoryItemEquipRequest.Length || packet.Span[0] != InventoryItemEquipRequest.HeaderType)
        {
            return ValueTask.CompletedTask;
        }

        InventoryItemEquipRequest request = packet;
        return this._action.EquipAsync(player, request.InventorySlot, request.Value);
    }
}
