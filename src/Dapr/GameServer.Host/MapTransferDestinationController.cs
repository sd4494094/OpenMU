// <copyright file="MapTransferDestinationController.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.Host;

using Microsoft.AspNetCore.Mvc;
using MUnique.OpenMU.Dapr.Common;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.ServerClients;

/// <summary>Announces a destination only when the map and a compatible listener are available.</summary>
[ApiController]
[Route(MapTransferApi.DestinationRoute)]
[ApiExplorerSettings(IgnoreApi = true)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[ServiceFilter(typeof(MapTransferAuthorizationFilter))]
public sealed class MapTransferDestinationController(
    IGameServer server,
    IGameServerContext context,
    GameServerStatePublisher listeners) : ControllerBase
{
    /// <summary>Probes a game server for a destination of an incoming map transfer.</summary>
    /// <param name="request">The shared configuration, map, and client to check.</param>
    /// <returns>The public destination, or a JSON null when it cannot accept the transfer.</returns>
    [HttpPost]
    public async ValueTask<JsonResult> GetDestinationAsync(MapTransferApi.DestinationRequest request)
    {
        if (server.ServerState != ServerState.Started
            || request.GameConfigurationId == Guid.Empty
            || context.Configuration.GetId() != request.GameConfigurationId
            || listeners.GetPublicEndPoint(request.Season, request.Episode, request.Language) is null
            || await context.GetMapAsync(request.MapNumber).ConfigureAwait(false) is not { IsTornDown: false, Definition.IsActive: true }
            || server.ServerState != ServerState.Started
            || listeners.GetPublicEndPoint(request.Season, request.Episode, request.Language) is not { } endpoint)
        {
            return new JsonResult(null);
        }

        return new JsonResult(new MapTransferApi.Destination(context.Id, endpoint.Address.ToString(), (ushort)endpoint.Port));
    }
}
