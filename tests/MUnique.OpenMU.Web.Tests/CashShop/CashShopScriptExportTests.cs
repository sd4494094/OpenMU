// <copyright file="CashShopScriptExportTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.CashShop;

using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.Web.AdminPanel.Operations.CashShop;
using BasicModel = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Verifies client/server catalog agreement and refusal of unrepresentable catalogs.
/// </summary>
[TestFixture]
public sealed class CashShopScriptExportTests
{
    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");
    private static readonly IReadOnlyDictionary<CashShopCoinType, string> Coins = new Dictionary<CashShopCoinType, string>
    {
        [CashShopCoinType.WCoinC] = "W币(C)",
        [CashShopCoinType.WCoinP] = "W币(P)",
        [CashShopCoinType.GoblinPoints] = "哥布林积分",
    };

    /// <summary>Checks all three currencies, localization, prices, IDs, quantities and deterministic output.</summary>
    [Test]
    public void ScriptsPreserveTheServerContract()
    {
        var catalog = CreateCatalog();
        var first = CashShopScriptExport.Write(catalog, Chinese, Coins);
        Assert.That(CashShopScriptExport.Write(catalog, Chinese, Coins), Is.EqualTo(first));
        var files = Read(first);
        var categories = Rows(files, "IBSCategory.txt");
        var packages = Rows(files, "IBSPackage.txt");
        var products = Rows(files, "IBSProduct.txt");
        Assert.That(categories, Has.Count.EqualTo(6));
        Assert.That(categories.All(row => row.Length == 7), Is.True);
        Assert.That(categories.All(row => row[3] == "201"), Is.True);
        Assert.That(categories.Select(row => row[1]), Does.Contain("哥布林积分"));
        Assert.That(packages.All(row => row.Length == 27), Is.True);
        Assert.That(products.All(row => row.Length == 17), Is.True);
        foreach (var source in catalog.Packages)
        {
            var row = packages.Single(row => row[2] == source.PackageSequence.ToString(CultureInfo.InvariantCulture));
            Assert.That(row[3], Is.EqualTo("中文商品"));
            Assert.That(row[5], Is.EqualTo(source.Products.Single().Price.ToString(CultureInfo.InvariantCulture)));
            Assert.That(row[8], Is.EqualTo(source.IsForSale ? "182" : "183"));
            Assert.That(row[9], Is.EqualTo(source.IsGiftable ? "184" : "185"));
            Assert.That(row[25], Is.EqualTo(source.CoinType switch { CashShopCoinType.WCoinC => "508", CashShopCoinType.WCoinP => "509", _ => "0" }));
            var product = products.Single(p => p[6] == source.Products.Single().PriceSequence.ToString(CultureInfo.InvariantCulture));
            Assert.That(product[3], Is.EqualTo("3"));
            Assert.That(product[13], Is.EqualTo("7181")); // group 14, item 13: Jewel of Bless.
        }

        Assert.That(files.Keys.Any(path => path == "Data/InGameShopScript/512.2012.084/IBSPackage.txt"), Is.True);
        Assert.That(files.Single(file => file.Key.EndsWith("IBSBanner.txt", StringComparison.Ordinal)).Value, Is.Empty);
        using var manifest = JsonDocument.Parse(files["cash-shop-source.json"]);
        Assert.That(manifest.RootElement.GetProperty("originalMuExCatalog").GetBoolean(), Is.False);
        Assert.That(manifest.RootElement.GetProperty("culture").GetString(), Is.EqualTo("zh-CN"));
    }

    /// <summary>Bundles charge the bundle price and have a single zero price selection.</summary>
    [Test]
    public void BundlePriceIsDistinctFromItsComponentPrices()
    {
        var catalog = CreateCatalog();
        var package = catalog.Packages.First();
        package.IsBundle = true;
        package.Price = 125;
        var files = Read(CashShopScriptExport.Write(catalog, Chinese, Coins));
        var row = Rows(files, "IBSPackage.txt").Single(row => row[2] == "10");
        Assert.That(row[5], Is.EqualTo("125"));
        Assert.That(row[22], Is.EqualTo("1"));
        Assert.That(row[23], Is.EqualTo("0"));
        Assert.That(package.Products.Single().Price, Is.EqualTo(20));
    }

    /// <summary>Different price options keep their own price sequences and prices.</summary>
    [Test]
    public void MultiplePriceOptionsKeepTheirPrices()
    {
        var catalog = CreateCatalog();
        var package = catalog.Packages.First();
        var second = CreateProduct(999, 40);
        second.ProductSequence = package.Products.Single().ProductSequence;
        package.Products.Add(second);
        var files = Read(CashShopScriptExport.Write(catalog, Chinese, Coins));
        var row = Rows(files, "IBSPackage.txt").Single(row => row[2] == "10");
        Assert.That(row[22], Is.EqualTo("2"));
        Assert.That(row[23], Is.EqualTo("110|999"));
        Assert.That(Rows(files, "IBSProduct.txt").Single(row => row[6] == "999")[5], Is.EqualTo("40"));
    }

    /// <summary>The global product lookup must not silently choose one of two different items or prices.</summary>
    [TestCase("price")]
    [TestCase("quantity")]
    [TestCase("level")]
    [TestCase("duration")]
    public void ConflictingGlobalProductKeysAreRefused(string field)
    {
        var catalog = CreateCatalog();
        var shared = catalog.Packages.First().Products.Single();
        var duplicate = CreateProduct(shared.PriceSequence, shared.Price);
        duplicate.ProductSequence = shared.ProductSequence;
        switch (field)
        {
            case "price": duplicate.Price++; break;
            case "quantity": duplicate.Quantity++; break;
            case "level": duplicate.ItemLevel++; break;
            case "duration": duplicate.Duration = TimeSpan.FromDays(1); break;
        }

        catalog.Packages.Last().Products.Add(duplicate);
        catalog.Packages.Last().IsBundle = true;
        Assert.Throws<ArgumentException>(() => CashShopScriptExport.Write(catalog, Chinese, Coins));
    }

    /// <summary>Delimited text must never inject a row or column, or be silently truncated.</summary>
    [TestCase("bad@name")]
    [TestCase("bad\r\nname")]
    [TestCase("bad\0name")]
    public void UnsafeNamesAreRefused(string text)
    {
        var catalog = CreateCatalog();
        catalog.Packages.First().Name = new LocalizedString(text);
        Assert.Throws<ArgumentException>(() => CashShopScriptExport.Write(catalog, Chinese, Coins));
    }

    /// <summary>Numbers which cannot address products or valid client items are rejected.</summary>
    [TestCase("duplicate-package")]
    [TestCase("negative-price")]
    [TestCase("invalid-item")]
    [TestCase("different-option-product")]
    [TestCase("long-name")]
    [TestCase("fractional-duration")]
    public void InvalidCatalogsAreRefused(string kind)
    {
        var catalog = CreateCatalog();
        var package = catalog.Packages.First();
        switch (kind)
        {
            case "duplicate-package": catalog.Packages.Last().PackageSequence = package.PackageSequence; break;
            case "negative-price": package.Products.Single().Price = -1; break;
            case "invalid-item": package.Products.Single().ItemDefinition!.Number = 512; break;
            case "different-option-product": package.Products.Add(CreateProduct(999, 40)); break;
            case "fractional-duration": package.Products.Single().Duration = TimeSpan.FromMilliseconds(1500); break;
            case "long-name": package.Name = new LocalizedString(new string('x', 512)); break;
        }

        Assert.Throws<ArgumentException>(() => CashShopScriptExport.Write(catalog, Chinese, Coins));
    }

    /// <summary>Tests the actual entire default Season 6 catalog rather than a hand-picked sample.</summary>
    [Test]
    public async Task EntireSeasonSixCatalogExportsAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        var initialization = new MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.DataInitialization(provider, NullLoggerFactory.Instance);
        await initialization.CreateInitialDataAsync(1, true).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var catalog = configuration.CashShopConfiguration!;
        var bytes = CashShopScriptExport.Write(catalog, Chinese, Coins);
        var files = Read(bytes);
        var rows = Rows(files, "IBSPackage.txt");
        Assert.That(rows, Has.Count.EqualTo(catalog.Packages.Count));
        foreach (var package in catalog.Packages)
        {
            var row = rows.Single(row => int.Parse(row[2], CultureInfo.InvariantCulture) == package.PackageSequence);
            Assert.That(row[8], Is.EqualTo(package.IsForSale ? "182" : "183"));
            Assert.That(row[5], Is.EqualTo((package.IsBundle ? package.Price : package.Products.Min(p => p.Price)).ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static BasicModel.CashShopConfiguration CreateCatalog()
    {
        var catalog = new BasicModel.CashShopConfiguration
        {
            ScriptSaleZone = 512, ScriptYear = 2012, ScriptYearId = 84,
            BannerSaleZone = 583, BannerYear = 2011, BannerYearId = 1,
        };
        foreach (var coin in Enum.GetValues<CashShopCoinType>())
        {
            var package = new BasicModel.CashShopPackage
            {
                PackageSequence = 10 + (int)coin,
                Name = new LocalizedString("中文商品"), CoinType = coin, Price = 500,
                IsForSale = coin != CashShopCoinType.WCoinP, IsGiftable = coin != CashShopCoinType.GoblinPoints,
            };
            package.Products.Add(CreateProduct(110 + (int)coin, 20 + (int)coin));
            catalog.Packages.Add(package);
        }

        return catalog;
    }

    private static BasicModel.CashShopProduct CreateProduct(int sequence, int price)
        => new()
        {
            ProductSequence = sequence, PriceSequence = sequence, Price = price, Quantity = 3,
            ItemDefinition = new ItemDefinition { Group = 14, Number = 13, Name = new LocalizedString("祝福宝石") },
        };

    private static Dictionary<string, byte[]> Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(entry => entry.FullName, entry =>
        {
            using var input = entry.Open();
            using var buffer = new MemoryStream();
            input.CopyTo(buffer);
            return buffer.ToArray();
        });
    }

    private static List<string[]> Rows(IReadOnlyDictionary<string, byte[]> files, string filename)
    {
        var bytes = files.Single(file => file.Key.EndsWith('/' + filename, StringComparison.Ordinal)).Value;
        Assert.That(bytes.Take(2), Is.EqualTo(new byte[] { 0xFF, 0xFE }));
        return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2).Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('@')).ToList();
    }
}
