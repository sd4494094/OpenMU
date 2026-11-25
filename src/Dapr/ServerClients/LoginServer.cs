// <copyright file="LoginServer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.ServerClients;

using System.Threading;
using Dapr.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.Interfaces;

/// <summary>
/// Implementation of an <see cref="ILoginServer"/> which accesses the login server remotely over Dapr.
/// </summary>
public class LoginServer : ILoginServer, IMapTransferLoginServer
{
    private readonly DaprClient _daprClient;
    private readonly ILogger<LoginServer> _logger;
    private readonly string _targetAppId;
    private readonly string? _mapTransferToken;

    /// <summary>
    /// Initializes a new instance of the <see cref="LoginServer"/> class.
    /// </summary>
    /// <param name="daprClient">The dapr client.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configuration">The game host configuration.</param>
    public LoginServer(DaprClient daprClient, ILogger<LoginServer> logger, IConfiguration? configuration = null)
    {
        this._daprClient = daprClient;
        this._logger = logger;
        this._targetAppId = CentralServer.AppId;
        this._mapTransferToken = configuration?[MapTransferApi.TokenSetting];
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryReserveAccountAsync(string accountName, int reservationId)
        => await this._daprClient.InvokeMethodAsync<AccountReservationArguments, bool>(this._targetAppId, GetMethodName(nameof(this.TryReserveAccountAsync)), new AccountReservationArguments(accountName, reservationId)).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask ReleaseAccountReservationAsync(string accountName, int reservationId)
        => await this._daprClient.InvokeMethodAsync(this._targetAppId, GetMethodName(nameof(this.ReleaseAccountReservationAsync)), new AccountReservationArguments(accountName, reservationId)).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<bool> TryLoginAsync(string accountName, byte serverId)
    {
        try
        {
            return await this._daprClient.InvokeMethodAsync<LoginArguments, bool>(this._targetAppId, GetMethodName(nameof(this.TryLoginAsync)), new LoginArguments(accountName, serverId)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Unexpected error when trying to call TryLogin on the login server.");
            return false;
        }
    }

    /// <inheritdoc />
    public async ValueTask LogOffAsync(string accountName, byte serverId)
    {
        try
        {
            await this._daprClient.InvokeMethodAsync(this._targetAppId, GetMethodName(nameof(this.LogOffAsync)), new LoginArguments(accountName, serverId)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Unexpected error when trying to call LogOff on the login server.");
        }
    }

    /// <inheritdoc />
    public async ValueTask<Dictionary<string, byte>> GetSnapshotAsync()
    {
        try
        {
            return await this._daprClient.InvokeMethodAsync<Dictionary<string, byte>>(this._targetAppId, GetMethodName(nameof(this.GetSnapshotAsync))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Unexpected error when trying to call GetSnapshotAsync on the login server.");
        }

        return [];
    }

    /// <inheritdoc />
    public ValueTask<Guid?> BeginMapTransferAsync(string accountName, string characterName, byte sourceServerId, byte targetServerId)
        => this.InvokeMapTransferAsync<MapTransferApi.BeginRequest, Guid?>(nameof(this.BeginMapTransferAsync), new(accountName, characterName, sourceServerId, targetServerId));

    /// <inheritdoc />
    public ValueTask<string?> ClaimMapTransferAsync(Guid credential, string characterName, byte targetServerId)
        => this.InvokeMapTransferAsync<MapTransferApi.ClaimRequest, string?>(nameof(this.ClaimMapTransferAsync), new(credential, characterName, targetServerId));

    /// <inheritdoc />
    public ValueTask<bool> CancelMapTransferAsync(Guid credential, byte sourceServerId)
        => this.InvokeMapTransferAsync<MapTransferApi.CancelRequest, bool>(nameof(this.CancelMapTransferAsync), new(credential, sourceServerId));

    private static string GetMethodName(string name) => $"{CentralServer.LoginServerRoute}/{name}";

    private async ValueTask<TResponse> InvokeMapTransferAsync<TRequest, TResponse>(string operation, TRequest arguments)
    {
        if (string.IsNullOrWhiteSpace(this._mapTransferToken))
        {
            throw new InvalidOperationException("The map transfer service credential is missing.");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var request = this._daprClient.CreateInvokeMethodRequest(this._targetAppId, $"{MapTransferApi.Route}/{operation}", arguments);
        request.Headers.Add(MapTransferApi.TokenHeader, this._mapTransferToken);
        return await this._daprClient.InvokeMethodAsync<TResponse>(request, timeout.Token).ConfigureAwait(false);
    }
}
