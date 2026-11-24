// <copyright file="IMapHostLocator.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic;

/// <summary>
/// Locates the game server which hosts a game map for the players of other game servers.
/// </summary>
/// <remarks>
/// The maps which a game server hosts are defined by the <see cref="GameServerConfiguration.Maps"/>
/// of its server configuration. When a player should enter a map which its own game server doesn't
/// host, it can still enter it on another game server which hosts it. That makes it a map which
/// is shared by the game servers, e.g. for events which take place for all game servers at once.
/// </remarks>
public interface IMapHostLocator
{
    /// <summary>
    /// Gets the configured owner of a map that needs a single instance. The owner remains
    /// designated while stopped, so other game servers cannot start a second copy of its event.
    /// </summary>
    /// <param name="requester">The requesting game server.</param>
    /// <param name="mapNumber">The map number.</param>
    /// <returns>The owner and its availability, or <c>null</c> if no owner is known.</returns>
    (IGameServerContext Context, bool IsStarted)? GetConfiguredHost(IGameServerContext requester, ushort mapNumber) => null;

    /// <summary>
    /// Gets the game contexts in the requester's process that share its persistence provider.
    /// </summary>
    /// <param name="requester">The requesting game server.</param>
    /// <returns>The game contexts, including the requester.</returns>
    IEnumerable<IGameServerContext> GetLocalContexts(IGameServerContext requester) => [requester];

    /// <summary>
    /// Locates another game server which hosts the map with the specified number.
    /// </summary>
    /// <param name="requester">The context of the game server which doesn't host the map itself.</param>
    /// <param name="mapNumber">The map number.</param>
    /// <returns>The located host; or <c>null</c>, if no other available game server hosts the map.</returns>
    ValueTask<MapHost?> LocateAsync(IGameServerContext requester, ushort mapNumber);
}
