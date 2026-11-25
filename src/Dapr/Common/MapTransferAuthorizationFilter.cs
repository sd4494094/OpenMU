// <copyright file="MapTransferAuthorizationFilter.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Dapr.Common;

using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;
using MUnique.OpenMU.ServerClients;

/// <summary>Authenticates game hosts before binding an account transfer request.</summary>
public sealed class MapTransferAuthorizationFilter : IAuthorizationFilter
{
    private readonly byte[]? _tokenHash;

    /// <summary>Initializes a new instance of the <see cref="MapTransferAuthorizationFilter"/> class.</summary>
    /// <param name="configuration">The game or central host configuration.</param>
    public MapTransferAuthorizationFilter(IConfiguration configuration)
    {
        var token = configuration[MapTransferApi.TokenSetting];
        this._tokenHash = string.IsNullOrWhiteSpace(token) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    /// <inheritdoc />
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var supplied = context.HttpContext.Request.Headers[MapTransferApi.TokenHeader];
        if (this._tokenHash is null || supplied.Count != 1 || supplied[0] is not { Length: > 0 } token
            || !CryptographicOperations.FixedTimeEquals(this._tokenHash, SHA256.HashData(Encoding.UTF8.GetBytes(token))))
        {
            context.Result = new UnauthorizedResult();
        }
    }
}
