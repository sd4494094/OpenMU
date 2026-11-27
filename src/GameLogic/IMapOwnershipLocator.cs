// <copyright file="IMapOwnershipLocator.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic;

/// <summary>
/// Resolves the single game server which owns a configured shared map.
/// </summary>
public interface IMapOwnershipLocator
{
    /// <summary>
    /// Gets the configured owner of a map, including when that server is currently stopped.
    /// </summary>
    /// <param name="requester">The game server requesting the owner.</param>
    /// <param name="mapNumber">The map number.</param>
    /// <returns>The configured server identifier, or <see langword="null"/> if no server hosts the map.</returns>
    ValueTask<byte?> GetOwnerServerIdAsync(IGameServerContext requester, ushort mapNumber);
}
