// <copyright file="CashShopScriptController.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel.API;

using System.Globalization;
using System.Threading;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Web.AdminPanel.Auth;
using MUnique.OpenMU.Web.AdminPanel.Operations;
using MUnique.OpenMU.Web.AdminPanel.Operations.CashShop;
using MUnique.OpenMU.Web.AdminPanel.Services;

/// <summary>
/// Lets an authenticated administrator download MuMain scripts from the saved cash shop catalog.
/// </summary>
/// <param name="persistence">The persistence context provider.</param>
/// <param name="configurations">The configuration source.</param>
[ApiController]
[Route("download/cash-shop-script")]
[Authorize(Policy = AdminPolicies.AuthenticatedAdministrator)]
public sealed class CashShopScriptController(IPersistenceContextProvider persistence, IDataSource<GameConfiguration> configurations) : ControllerBase
{
    private static readonly SemaphoreSlim ExportGate = new(1, 1);

    /// <summary>
    /// Downloads the scripts; another simultaneous export is refused rather than queued.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The catalog ZIP, or a problem response if the catalog cannot be represented.</returns>
    [HttpGet]
    public async ValueTask<IActionResult> DownloadAsync(CancellationToken cancellationToken)
    {
        if (!await ExportGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            this.Response.Headers.RetryAfter = "1";
            return this.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        try
        {
            var coinNames = Enum.GetValues<CashShopCoinType>().ToDictionary(coin => coin, CashShopCoinCaption.Get);
            var archive = await new CashShopScriptOperations(persistence, configurations)
                .ExportAsync(CultureInfo.CurrentUICulture, coinNames, cancellationToken).ConfigureAwait(false);
            this.Response.Headers.CacheControl = "no-store";
            return this.File(archive, "application/zip", "OpenMU-MuMain-cash-shop.zip");
        }
        catch (ArgumentException ex)
        {
            return this.Problem(statusCode: StatusCodes.Status409Conflict, detail: ex.Message);
        }
        catch (AdminOperationException ex) when (ex.Kind == AdminOperationErrorKind.Unavailable)
        {
            return this.Problem(statusCode: StatusCodes.Status409Conflict, detail: ex.Message);
        }
        finally
        {
            ExportGate.Release();
        }
    }
}
