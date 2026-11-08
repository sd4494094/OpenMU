// <copyright file="ResetCharacterPointRequestHandlerPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MessageHandler;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Resets;
using MUnique.OpenMU.Network.Packets.ClientToServer;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Handles the native stat reset confirmation (F2/00).
/// </summary>
[PlugIn]
[Guid("82bd8b2f-aab5-456c-964b-cf9583519d11")]
[Display(Name = nameof(PlugInResources.ResetCharacterPointRequestHandlerPlugIn_Name), Description = nameof(PlugInResources.ResetCharacterPointRequestHandlerPlugIn_Description), ResourceType = typeof(PlugInResources))]
internal class ResetCharacterPointRequestHandlerPlugIn : IPacketHandlerPlugIn
{
    /// <inheritdoc />
    public bool IsEncryptionExpected => false;

    /// <inheritdoc />
    public byte Key => ResetCharacterPointRequest.Code;

    /// <inheritdoc />
    public async ValueTask HandlePacketAsync(Player player, Memory<byte> packet)
    {
        if (packet.Length == StatAdvisorRequestExtended.Length && packet.Span[0] == 0xC1
            && packet.Span[1] == StatAdvisorRequestExtended.Length && packet.Span[2] == StatAdvisorRequestExtended.Code
            && packet.Span[3] == StatAdvisorRequestExtended.SubCode)
        {
            StatAdvisorRequestExtended request = packet;
            if (packet.Span[37] != 0 || packet.Span[70] != 0)
            {
                return;
            }

            var values = new[] { request.Strength, request.Agility, request.Vitality, request.Energy, request.Leadership };
            if (values.Any(value => value > int.MaxValue))
            {
                return;
            }

            await StatAdvisorAction.HandleAsync(player, request.Operation, request.SessionId, request.Token,
                values.Select(value => (int)value).ToArray()).ConfigureAwait(false);
            return;
        }

        if (packet.Length != ResetCharacterPointRequest.Length
            || packet.Span[0] != 0xC1
            || packet.Span[1] != ResetCharacterPointRequest.Length
            || packet.Span[2] != ResetCharacterPointRequest.Code
            || packet.Span[3] != ResetCharacterPointRequest.SubCode)
        {
            return;
        }

        if (StatAdvisorAction.IsActive(player))
        {
            return;
        }

        await new ResetStatsAction(player).ResetStatsFromNpcAsync().ConfigureAwait(false);
    }
}
