// <copyright file="CashShopScriptExport.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel.Operations.CashShop;

using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MUnique.OpenMU.DataModel.Configuration;

/// <summary>
/// Writes a MuMain catalog from the server's packages, without importing third-party scripts.
/// </summary>
public static class CashShopScriptExport
{
    private const int CategoryOffset = 100;
    private const int ItemGroupSize = 512;
    private const int DialogTextCapacity = 255;
    private const int NoItemPreview = ushort.MaxValue;
    private const int NormalPackage = 170;
    private const int NormalCapsule = 177;
    private const int ActivePackage = 181;
    private const int ForSale = 182;
    private const int NotForSale = 183;
    private const int Giftable = 184;
    private const int NotGiftable = 185;
    private const int NormalEvent = 200;
    private const int OpenCategory = 201;
    private const int ProductProperty = 141;
    private const int ActiveProduct = 144;
    private const int RequiredProperty = 145;
    private const int QuantityProperty = 11;
    private const int PeriodProperty = 2;
    private const int SecondsUnit = 386;
    private const string StartDate = "20000101000000";
    private const string EndDate = "20991231235959";

    /// <summary>
    /// Writes the versioned script files and a source manifest in a ZIP archive.
    /// </summary>
    /// <param name="configuration">The saved server catalog.</param>
    /// <param name="culture">The culture of the package and item names.</param>
    /// <param name="coinNames">Localized captions for all three coin types.</param>
    /// <returns>The archive; extract into an isolated MuMain client folder first.</returns>
    /// <remarks>
    /// The result is a newly generated MuMain catalog, not the original Webzen or MuEx catalog.
    /// Conflicting global product keys cannot be represented by MuMain and are rejected.
    /// The fixed script property and flag codes follow MuMain's shop reader contract.
    /// </remarks>
    public static byte[] Write(CashShopConfiguration configuration, CultureInfo culture, IReadOnlyDictionary<CashShopCoinType, string> coinNames)
    {
        ValidateCatalog(configuration);
        var version = Version(configuration.ScriptSaleZone, configuration.ScriptYear, configuration.ScriptYearId);
        var bannerVersion = Version(configuration.BannerSaleZone, configuration.BannerYear, configuration.BannerYearId);
        var packages = configuration.Packages.OrderBy(p => p.PackageSequence).ToList();
        var category = new StringBuilder();
        foreach (var coin in packages.Select(p => p.CoinType).Distinct().Order())
        {
            var zone = (int)coin + 1;
            var caption = Text(coinNames[coin], 128);
            category.Append(Row(zone, caption, NormalEvent, OpenCategory, 0, zone, 1));
            category.Append(Row(CategoryOffset + zone, caption, NormalEvent, OpenCategory, zone, 1, 0));
        }

        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"Data/InGameShopScript/{version}/IBSCategory.txt"] = Encode(category.ToString()),
            [$"Data/InGameShopScript/{version}/IBSPackage.txt"] = Encode(string.Concat(packages.Select(p => PackageRow(p, culture, coinNames[p.CoinType])))),
            [$"Data/InGameShopScript/{version}/IBSProduct.txt"] = Encode(ProductRows(packages, culture)),

            // An empty banner list intentionally provides no advertising and needs no external download.
            [$"Data/InGameShopBanner/{bannerVersion}/IBSBanner.txt"] = [],
        };
        files["cash-shop-source.json"] = Manifest(configuration, culture, version, bannerVersion, files);
        return Zip(files);
    }

    private static string Version(short zone, short year, short id)
    {
        if (zone < 0 || year < 0 || id < 0)
        {
            throw new ArgumentException("Cash shop version components must not be negative.");
        }

        return FormattableString.Invariant($"{zone:000}.{year:0000}.{id:000}");
    }

    private static void ValidateCatalog(CashShopConfiguration configuration)
    {
        if (configuration.Packages.Count == 0 || configuration.Packages.GroupBy(p => p.PackageSequence).Any(g => g.Count() > 1))
        {
            throw new ArgumentException("The catalog needs packages with unique sequence numbers.");
        }

        foreach (var package in configuration.Packages)
        {
            if (package.PackageSequence < 1 || package.Price < 0 || !Enum.IsDefined(package.CoinType)
                || package.Products.Count == 0 || package.Products.GroupBy(p => p.PriceSequence).Any(g => g.Count() > 1))
            {
                throw new ArgumentException($"Invalid cash shop package {package.PackageSequence}.");
            }

            if (!package.IsBundle && package.Products.Select(p => p.ProductSequence).Distinct().Count() != 1)
            {
                throw new ArgumentException($"MuMain price options must share a product sequence (package {package.PackageSequence}).");
            }

            foreach (var product in package.Products)
            {
                ValidateProduct(product);
            }
        }
    }

    private static void ValidateProduct(CashShopProduct product)
    {
        if (product.ProductSequence < 1 || product.PriceSequence < 1 || product.Price < 0
            || product.Quantity < 1 || product.Duration < TimeSpan.Zero || product.Duration.TotalSeconds > int.MaxValue
            || product.Duration.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentException($"Invalid cash shop product {product.ProductSequence}/{product.PriceSequence}.");
        }

        if (product.ItemDefinition is { } item && (item.Group > 15 || item.Number < 0 || item.Number >= ItemGroupSize))
        {
            throw new ArgumentException($"Item {item.Group}/{item.Number} cannot be represented by MuMain.");
        }
    }

    private static string PackageRow(CashShopPackage package, CultureInfo culture, string coinName)
    {
        var products = package.Products.OrderBy(p => p.PriceSequence).ToList();
        var first = products[0];
        var name = Text(package.Name.GetTranslation(culture) ?? string.Empty, DialogTextCapacity);
        var description = Text(string.Join("#", products.Select(p => $"{ProductName(p, culture)} × {p.Quantity.ToString(CultureInfo.InvariantCulture)}")), 2048);
        var prices = package.IsBundle ? "0" : string.Join("|", products.Select(p => p.PriceSequence));
        var priceCount = package.IsBundle ? 1 : products.Count;
        var displayPrice = package.IsBundle ? package.Price : products.Min(p => p.Price);
        return Row(
            CategoryOffset + (int)package.CoinType + 1,
            package.PackageSequence,
            package.PackageSequence,
            name,
            NormalPackage,
            displayPrice,
            description,
            string.Empty,
            package.IsForSale ? ForSale : NotForSale,
            package.IsGiftable ? Giftable : NotGiftable,
            StartDate,
            EndDate,
            NormalCapsule,
            1,
            Text(coinName, 256),
            Text(coinName, 64),
            ActivePackage,
            NormalEvent,
            products.Count,
            string.Join("|", products.Select(p => p.ProductSequence).Distinct()),
            ItemCode(first),
            0,
            priceCount,
            prices,
            0,
            ScriptCoin(package.CoinType),
            0);
    }

    private static string ProductRows(IReadOnlyList<CashShopPackage> packages, CultureInfo culture)
    {
        var rows = new StringBuilder();
        foreach (var group in packages.SelectMany(p => p.Products).GroupBy(p => (p.ProductSequence, p.PriceSequence))
                     .OrderBy(g => g.Key.ProductSequence).ThenBy(g => g.Key.PriceSequence))
        {
            var alternatives = group.Select(p => ProductRow(p, culture, QuantityProperty, p.Quantity, 0)).Distinct().ToList();
            var durations = group.Select(p => p.Duration).Distinct().ToList();
            if (alternatives.Count != 1 || durations.Count != 1)
            {
                throw new ArgumentException($"Conflicting global product key {group.Key.ProductSequence}/{group.Key.PriceSequence}.");
            }

            rows.Append(alternatives[0]);
            var product = group.First();
            if (product.Duration > TimeSpan.Zero)
            {
                rows.Append(ProductRow(product, culture, PeriodProperty, (int)product.Duration.TotalSeconds, SecondsUnit));
            }
        }

        return rows.ToString();
    }

    private static string ProductRow(CashShopProduct product, CultureInfo culture, int property, int value, int unit)
        => Row(
            product.ProductSequence,
            ProductName(product, culture),
            string.Empty,
            value,
            string.Empty,
            product.Price,
            product.PriceSequence,
            ProductProperty,
            RequiredProperty,
            1,
            ActiveProduct,
            0,
            0,
            ItemCode(product),
            property,
            0,
            unit);

    private static string ProductName(CashShopProduct product, CultureInfo culture)
    {
        var name = product.ItemDefinition?.Name.GetTranslation(culture)
            ?? product.ProductSequence.ToString(CultureInfo.InvariantCulture);
        return Text(product.ItemLevel == 0 ? name : $"{name} +{product.ItemLevel.ToString(CultureInfo.InvariantCulture)}", 128);
    }

    private static int ItemCode(CashShopProduct product)
        => product.ItemDefinition is { } item ? (item.Group * ItemGroupSize) + item.Number : NoItemPreview;

    private static int ScriptCoin(CashShopCoinType coin) => coin switch
    {
        CashShopCoinType.WCoinC => 508,
        CashShopCoinType.WCoinP => 509,
        CashShopCoinType.GoblinPoints => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(coin)),
    };

    private static string Text(string value, int capacity)
    {
        if (value.Length >= capacity || value.IndexOfAny(['@', '\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("Catalog text contains a script delimiter or exceeds the client field capacity.");
        }

        return value;
    }

    private static string Row(params object[] fields)
        => string.Join("@", fields.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture))) + "\r\n";

    private static byte[] Encode(string text)
        => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)];

    private static byte[] Manifest(CashShopConfiguration configuration, CultureInfo culture, string version, string bannerVersion, IReadOnlyDictionary<string, byte[]> files)
        => JsonSerializer.SerializeToUtf8Bytes(new
        {
            source = "OpenMU",
            target = "MuMain",
            originalMuExCatalog = false,
            culture = culture.Name,
            scriptVersion = version,
            bannerVersion,
            files = files.Select(pair => new { path = pair.Key, sha256 = Convert.ToHexStringLower(SHA256.HashData(pair.Value)) }),
            packages = configuration.Packages.OrderBy(p => p.PackageSequence).Select(p => new
            {
                p.PackageSequence,
                p.CoinType,
                p.Price,
                p.IsBundle,
                p.IsForSale,
                p.IsGiftable,
                products = p.Products.OrderBy(product => product.PriceSequence).Select(product => new
                {
                    product.ProductSequence,
                    product.PriceSequence,
                    product.Price,
                    itemGroup = product.ItemDefinition?.Group,
                    itemNumber = product.ItemDefinition?.Number,
                    product.ItemLevel,
                    product.Quantity,
                    durationSeconds = product.Duration.TotalSeconds,
                }),
            }),
        });

    private static byte[] Zip(IReadOnlyDictionary<string, byte[]> files)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in files)
            {
                var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var output = entry.Open();
                output.Write(bytes);
            }
        }

        return buffer.ToArray();
    }
}
