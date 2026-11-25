// <copyright file="MapTransferAuthenticationHandlerPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MessageHandler.Login;

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameServer.RemoteView;
using MUnique.OpenMU.Network.Packets.ClientToServer;
using MUnique.OpenMU.Network.Packets.ServerToClient;
using MUnique.OpenMU.Network.PlugIns;
using MUnique.OpenMU.Network.Xor;
using MUnique.OpenMU.PlugIns;

/// <summary>Handles the transfer authentication packet from the open source client.</summary>
[PlugIn]
[Guid("F53A06D9-16CA-4BBF-B02B-478F8C00C688")]
[MinimumClient(106, 3, ClientLanguage.Invariant)]
internal sealed class MapTransferAuthenticationHandlerPlugIn : IPacketHandlerPlugIn
{
    private const int MuMainCharacterNameLength = 10;

    private readonly AcceptMapTransferAction _action = new();

    /// <inheritdoc/>
    public byte Key => ServerChangeAuthentication.Code;

    /// <inheritdoc/>
    public bool IsEncryptionExpected => false; // Validate the header here without logging credentials on refusal.

    /// <inheritdoc/>
    public async ValueTask HandlePacketAsync(Player player, Memory<byte> packet)
    {
        if (player is not RemotePlayer remote || player.PlayerState.CurrentState != PlayerState.LoginScreen)
        {
            return;
        }

        if (ParseRequest(remote, packet) is not { } request)
        {
            await player.DisconnectAsync().ConfigureAwait(false);
            return;
        }

        if (await this._action.AcceptAsync(player, request.Credential, request.CharacterName).ConfigureAwait(false))
        {
            await remote.Connection.SendMapServerChangeResultAsync().ConfigureAwait(false);
        }
    }

    private static (Guid Credential, string CharacterName)? ParseRequest(RemotePlayer player, Memory<byte> packet)
    {
        if (packet.Length != ServerChangeAuthentication.Length || packet.Span[0] != ServerChangeAuthentication.HeaderType)
        {
            return null;
        }

        ServerChangeAuthentication message = packet;
        if (message.Header.SubCode != ServerChangeAuthentication.SubCode
            || !message.ClientVersion.SequenceEqual(ClientVersionResolver.Resolve(player.ClientVersion)))
        {
            return null;
        }

        Span<byte> name = stackalloc byte[MuMainCharacterNameLength];
        message.CharacterNameXor3[..MuMainCharacterNameLength].CopyTo(name);
        new Xor3Decryptor(0).Decrypt(name);
        int terminator = name.IndexOf((byte)0);
        var characterName = Encoding.UTF8.GetString(terminator < 0 ? name : name[..terminator]);
        if (string.IsNullOrWhiteSpace(characterName))
        {
            return null;
        }

        Span<byte> credential = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(credential, message.AuthCode1);
        BinaryPrimitives.WriteUInt32LittleEndian(credential[4..], message.AuthCode2);
        BinaryPrimitives.WriteUInt32LittleEndian(credential[8..], message.AuthCode3);
        BinaryPrimitives.WriteUInt32LittleEndian(credential[12..], message.AuthCode4);
        return (new Guid(credential), characterName);
    }
}
