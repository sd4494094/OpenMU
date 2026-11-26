// <copyright file="CharacterInventoryRequestPacketHandlerPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MessageHandler.Character;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlayerActions.Character;
using MUnique.OpenMU.Network.Packets.ClientToServer;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Handles the encrypted character inventory refresh request.
/// </summary>
[PlugIn]
[Display(Name = nameof(PlugInResources.CharacterInventoryRequestPacketHandlerPlugIn_Name), Description = nameof(PlugInResources.CharacterInventoryRequestPacketHandlerPlugIn_Description), ResourceType = typeof(PlugInResources))]
[Guid("e41f890b-4cb1-434d-a4a7-6d0617967b12")]
[BelongsToGroup(CharacterGroupHandlerPlugIn.GroupKey)]
internal class CharacterInventoryRequestPacketHandlerPlugIn : ISubPacketHandlerPlugIn
{
    private readonly RequestInventoryAction _action = new();

    /// <inheritdoc />
    public bool IsEncryptionExpected => true;

    /// <inheritdoc />
    public byte Key => InventoryRequest.SubCode;

    /// <inheritdoc />
    public async ValueTask HandlePacketAsync(Player player, Memory<byte> packet)
    {
        if (packet.Length != InventoryRequest.Length)
        {
            return;
        }

        await this._action.RequestInventoryAsync(player).ConfigureAwait(false);
    }
}
