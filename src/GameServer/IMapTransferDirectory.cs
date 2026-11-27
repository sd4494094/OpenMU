// <copyright file="IMapTransferDirectory.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer;

using System.Net;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.Network.PlugIns;

/// <summary>Finds the live client listener of another game server for a map transfer.</summary>
public interface IMapTransferDirectory
{
    /// <summary>Finds a compatible host of the requested map in the shared configuration.</summary>
    /// <param name="source">The game server requesting the transfer.</param>
    /// <param name="mapNumber">The destination map.</param>
    /// <param name="version">The client version on the source session.</param>
    /// <returns>The destination server and listener, or null if no host can accept the transfer.</returns>
    ValueTask<(byte ServerId, IPEndPoint EndPoint)?> FindDestinationAsync(IGameServerContext source, ushort mapNumber, ClientVersion version);

    /// <summary>Finds a compatible destination on a specific game server.</summary>
    /// <param name="source">The game server requesting the transfer.</param>
    /// <param name="serverId">The designated destination server.</param>
    /// <param name="mapNumber">The destination map.</param>
    /// <param name="version">The client version on the source session.</param>
    /// <returns>The destination listener, or null if that server cannot accept the transfer.</returns>
    ValueTask<(byte ServerId, IPEndPoint EndPoint)?> FindDestinationOnServerAsync(IGameServerContext source, byte serverId, ushort mapNumber, ClientVersion version)
        => this.FindDestinationAsync(source, mapNumber, version);
}
