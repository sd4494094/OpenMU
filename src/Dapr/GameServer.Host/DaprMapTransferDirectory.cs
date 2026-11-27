// <copyright file="DaprMapTransferDirectory.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.Host;

using System.Net;
using System.Net.Sockets;
using System.Threading;
using global::Dapr.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.Network.PlugIns;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.ServerClients;

/// <summary>Queries configured map hosts through their authenticated Dapr endpoints.</summary>
public sealed class DaprMapTransferDirectory(
    DaprClient daprClient,
    IConfiguration configuration,
    ILogger<DaprMapTransferDirectory> logger) : IMapTransferDirectory
{
    /// <inheritdoc />
    public async ValueTask<(byte ServerId, IPEndPoint EndPoint)?> FindDestinationAsync(IGameServerContext source, ushort mapNumber, ClientVersion version)
    {
        var configurationId = source.Configuration.GetId();
        if (configuration[MapTransferApi.TokenSetting] is not { Length: > 0 } token || string.IsNullOrWhiteSpace(token)
            || configurationId == Guid.Empty)
        {
            return null;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            using var context = source.PersistenceContextProvider.CreateNewConfigurationContext();
            var definitions = await context.GetAsync<GameServerDefinition>(timeout.Token).ConfigureAwait(false);
            var request = new MapTransferApi.DestinationRequest(configurationId, mapNumber, version.Season, version.Episode, (byte)version.Language);
            var candidates = definitions
                .Where(definition => definition.ServerID != source.Id
                    && definition.GameConfiguration?.GetId() == configurationId
                    && definition.ServerConfiguration?.Maps.Any(map => map.Number == mapNumber && map.IsActive) == true)
                .Select(definition => definition.ServerID)
                .Distinct()
                .Order()
                .ToArray();

            // A stopped host must not prevent another compatible host from being queried.
            var probes = candidates.Select(id => this.ProbeAsync(id, request, token, timeout.Token));
            var results = await Task.WhenAll(probes).ConfigureAwait(false);
            return results.FirstOrDefault(result => result is not null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not locate another game server for map {MapNumber}.", mapNumber);
            return null;
        }
    }

    /// <inheritdoc />
    public async ValueTask<(byte ServerId, IPEndPoint EndPoint)?> FindDestinationOnServerAsync(IGameServerContext source, byte serverId, ushort mapNumber, ClientVersion version)
    {
        var configurationId = source.Configuration.GetId();
        if (serverId == source.Id
            || configuration[MapTransferApi.TokenSetting] is not { Length: > 0 } token
            || string.IsNullOrWhiteSpace(token)
            || configurationId == Guid.Empty)
        {
            return null;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            using var context = source.PersistenceContextProvider.CreateNewConfigurationContext();
            var definitions = await context.GetAsync<GameServerDefinition>(timeout.Token).ConfigureAwait(false);
            var isConfiguredHost = definitions.Any(definition => definition.ServerID == serverId
                && definition.GameConfiguration?.GetId() == configurationId
                && definition.ServerConfiguration?.Maps.Any(map => map.Number == mapNumber && map.IsActive) == true);
            if (!isConfiguredHost)
            {
                return null;
            }

            var request = new MapTransferApi.DestinationRequest(configurationId, mapNumber, version.Season, version.Episode, (byte)version.Language);
            return await this.ProbeAsync(serverId, request, token, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not locate configured game server {ServerId} for map {MapNumber}.", serverId, mapNumber);
            return null;
        }
    }

    private async Task<(byte ServerId, IPEndPoint EndPoint)?> ProbeAsync(byte serverId, MapTransferApi.DestinationRequest arguments, string token, CancellationToken cancellationToken)
    {
        try
        {
            using var request = daprClient.CreateInvokeMethodRequest($"game-server-{serverId}", MapTransferApi.DestinationRoute, arguments);
            request.Headers.Add(MapTransferApi.TokenHeader, token);
            var result = await daprClient.InvokeMethodAsync<MapTransferApi.Destination?>(request, cancellationToken).ConfigureAwait(false);
            if (result is { Port: > 0 } && result.ServerId == serverId && IPAddress.TryParse(result.Address, out var address)
                && address.AddressFamily == AddressFamily.InterNetwork && !address.Equals(IPAddress.Any))
            {
                return (serverId, new IPEndPoint(address, result.Port));
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Game server {ServerId} did not resolve a destination for map {MapNumber}.", serverId, arguments.MapNumber);
        }

        return null;
    }
}
