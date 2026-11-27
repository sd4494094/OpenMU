// <copyright file="DaprMapOwnershipLocator.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.Host;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.Persistence;

/// <summary>Resolves a stable owner for shared maps from the deployment's server configuration.</summary>
public sealed class DaprMapOwnershipLocator(IPersistenceContextProvider persistenceContextProvider) : IMapOwnershipLocator
{
    /// <inheritdoc />
    public async ValueTask<byte?> GetOwnerServerIdAsync(IGameServerContext requester, ushort mapNumber)
    {
        var configurationId = requester.Configuration.GetId();
        if (configurationId == Guid.Empty)
        {
            return null;
        }

        using var context = persistenceContextProvider.CreateNewConfigurationContext();
        var definitions = await context.GetAsync<GameServerDefinition>().ConfigureAwait(false);
        return definitions
            .Where(definition => definition.GameConfiguration?.GetId() == configurationId
                && definition.ServerConfiguration?.Maps.Any(map => map.Number == mapNumber && map.IsActive) == true)
            .Select(definition => definition.ServerID)
            .Order()
            .Select(serverId => (byte?)serverId)
            .FirstOrDefault();
    }
}
