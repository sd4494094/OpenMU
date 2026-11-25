// <copyright file="MapTransferController.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.CentralServer.Host;

using Microsoft.AspNetCore.Mvc;
using MUnique.OpenMU.Dapr.Common;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.ServerClients;

/// <summary>Accepts map-transfer requests from authorized game hosts.</summary>
[ApiController]
[Route(MapTransferApi.Route)]
[ApiExplorerSettings(IgnoreApi = true)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[ServiceFilter(typeof(MapTransferAuthorizationFilter))]
public sealed class MapTransferController(IMapTransferLoginServer loginServer) : ControllerBase
{
    /// <summary>Reserves the account for the destination.</summary>
    /// <param name="request">The source account, character and target.</param>
    /// <returns>The credential or a JSON null when refused.</returns>
    [HttpPost(nameof(IMapTransferLoginServer.BeginMapTransferAsync))]
    public async ValueTask<JsonResult> BeginMapTransferAsync(MapTransferApi.BeginRequest request)
        => new(await loginServer.BeginMapTransferAsync(request.AccountName, request.CharacterName, request.SourceServerId, request.TargetServerId).ConfigureAwait(false));

    /// <summary>Consumes the credential on the destination.</summary>
    /// <param name="request">The credential, character and receiving game server.</param>
    /// <returns>The canonical account name or a JSON null when refused.</returns>
    [HttpPost(nameof(IMapTransferLoginServer.ClaimMapTransferAsync))]
    public async ValueTask<JsonResult> ClaimMapTransferAsync(MapTransferApi.ClaimRequest request)
        => new(await loginServer.ClaimMapTransferAsync(request.Credential, request.CharacterName, request.TargetServerId).ConfigureAwait(false));

    /// <summary>Releases a reservation abandoned by the source.</summary>
    /// <param name="request">The credential and source game server.</param>
    /// <returns>Whether the reservation was canceled.</returns>
    [HttpPost(nameof(IMapTransferLoginServer.CancelMapTransferAsync))]
    public ValueTask<bool> CancelMapTransferAsync(MapTransferApi.CancelRequest request)
        => loginServer.CancelMapTransferAsync(request.Credential, request.SourceServerId);
}
