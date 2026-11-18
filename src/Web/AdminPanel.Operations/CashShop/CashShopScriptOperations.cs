// <copyright file="CashShopScriptOperations.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel.Operations.CashShop;

using System.Globalization;
using System.Threading;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Persistence;

/// <summary>
/// Exports the saved catalog while preserving pending edits in the admin panel.
/// </summary>
/// <param name="persistence">The persistence context provider.</param>
/// <param name="configurations">The configuration edited by the admin panel.</param>
public sealed class CashShopScriptOperations(IPersistenceContextProvider persistence, IDataSource<GameConfiguration> configurations)
{
    /// <summary>
    /// Exports the configured client scripts; unsaved editor changes are not included.
    /// </summary>
    /// <param name="culture">The language of the exported names.</param>
    /// <param name="coinNames">The localized coin captions.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The ZIP archive.</returns>
    [NotExposedToAgents("Binary client-installation archive; catalog inspection is already available through the cash shop read tools.")]
    public async ValueTask<byte[]> ExportAsync(CultureInfo culture, IReadOnlyDictionary<CashShopCoinType, string> coinNames, CancellationToken cancellationToken)
    {
        var owner = await configurations.GetOwnerAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        using var context = persistence.CreateNewContext();
        var configuration = await context.GetByIdAsync<GameConfiguration>(owner.GetId(), cancellationToken).ConfigureAwait(false)
            ?? throw new AdminOperationException("The active configuration could not be loaded.", AdminOperationErrorKind.Unavailable);
        var catalog = configuration.CashShopConfiguration
            ?? throw new AdminOperationException("The cash shop is not configured.", AdminOperationErrorKind.Unavailable);
        cancellationToken.ThrowIfCancellationRequested();
        return CashShopScriptExport.Write(catalog, culture, coinNames);
    }
}
