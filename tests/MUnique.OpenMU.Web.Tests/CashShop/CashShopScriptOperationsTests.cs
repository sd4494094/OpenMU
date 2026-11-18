// <copyright file="CashShopScriptOperationsTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.CashShop;

using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.Web.AdminPanel.Operations.CashShop;

/// <summary>Ensures that downloads use saved values without discarding pending editor changes.</summary>
[TestFixture]
public sealed class CashShopScriptOperationsTests
{
    /// <summary>Exports stored prices while the GUI has unsaved prices.</summary>
    [TestCase(true)]
    [TestCase(false)]
    public async Task UnsavedPricesAreExcludedAsync(bool hasPendingEdits)
    {
        var provider = new InMemoryPersistenceContextProvider();
        using var storedContext = provider.CreateNewContext();
        var stored = CreateConfiguration(storedContext, 10);
        await storedContext.SaveChangesAsync().ConfigureAwait(false);
        using var editContext = new InMemoryPersistenceContextProvider().CreateNewContext();
        var edited = CreateConfiguration(editContext, hasPendingEdits ? 999 : 10);
        ((IIdentifiable)edited).Id = stored.GetId();
        var shared = new Mock<IContext>();
        shared.Setup(c => c.HasChanges).Returns(hasPendingEdits);
        var source = new Mock<IDataSource<GameConfiguration>>();
        source.Setup(s => s.GetOwnerAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(edited);
        source.Setup(s => s.GetContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(shared.Object);
        var coins = new Mock<IReadOnlyDictionary<CashShopCoinType, string>>();
        coins.SetupGet(c => c[It.IsAny<CashShopCoinType>()]).Returns("WCoin").Callback(() =>
        {
            // An edit starts after the initial clean-context check, while rows are generated.
            edited.CashShopConfiguration!.Packages.Single().Products.Single().Price = 999;
        });
        var bytes = await new CashShopScriptOperations(provider, source.Object)
            .ExportAsync(CultureInfo.InvariantCulture, coins.Object, CancellationToken.None).ConfigureAwait(false);
        using var buffer = new MemoryStream(bytes);
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        using var input = zip.Entries.Single(e => e.FullName.EndsWith("IBSPackage.txt", StringComparison.Ordinal)).Open();
        using var reader = new StreamReader(input, Encoding.Unicode);
        var text = await reader.ReadToEndAsync().ConfigureAwait(false);
        Assert.That(text.Split('@')[5], Is.EqualTo("10"));
        Assert.That(edited.CashShopConfiguration!.Packages.Single().Products.Single().Price, Is.EqualTo(999));
        shared.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        source.Verify(s => s.DiscardChangesAsync(), Times.Never);
    }

    private static GameConfiguration CreateConfiguration(IContext context, int price)
    {
        var configuration = context.CreateNew<GameConfiguration>();
        var catalog = context.CreateNew<CashShopConfiguration>();
        catalog.ScriptSaleZone = 512;
        catalog.ScriptYear = 2012;
        catalog.ScriptYearId = 84;
        configuration.CashShopConfiguration = catalog;
        var package = context.CreateNew<CashShopPackage>();
        package.PackageSequence = 1;
        package.Name = "Jewel";
        var product = context.CreateNew<CashShopProduct>();
        product.ProductSequence = 1;
        product.PriceSequence = 1;
        product.Price = price;
        product.ItemDefinition = context.CreateNew<ItemDefinition>();
        product.ItemDefinition.Name = "Jewel";
        package.Products.Add(product);
        catalog.Packages.Add(package);
        return configuration;
    }
}
