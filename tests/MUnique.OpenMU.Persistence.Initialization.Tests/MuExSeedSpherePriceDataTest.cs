// <copyright file="MuExSeedSpherePriceDataTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.Persistence.Initialization.Updates;
using MUnique.OpenMU.Persistence.InMemory;

/// <summary>
/// Verifies the optional MuEx seed sphere price data.
/// </summary>
[TestFixture]
internal class MuExSeedSpherePriceDataTest
{
    private static readonly int[] TierPrices = [1_050_000, 1_125_000, 1_200_000, 1_275_000, 1_350_000];

    /// <summary>
    /// Verifies that the option stays pending in a fresh configuration and only sets the target prices when applied.
    /// </summary>
    [Test]
    public async Task NewDatabaseKeepsDefaultPricesUntilSelectedAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new VersionSeasonSix.DataInitialization(provider, NullLoggerFactory.Instance).CreateInitialDataAsync(0, false).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var config = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var update = new SetMuExSeedSpherePricesUpdatePlugIn();
        var spheres = config.Items.Where(IsSphere).OrderBy(item => item.Number).ToArray();
        var controls = config.Items.Where(item => !IsSphere(item)).Select(item => (item.Value, item.PriceDefinition)).ToArray();
        var priceDefinitions = config.ItemPriceDefinitions.ToArray();

        Assert.That(update.IsMandatory, Is.False);
        Assert.That(update.RunsOnNewDatabase, Is.True);
        Assert.That(spheres.Select(item => item.Value), Is.All.Zero);
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Any(entry => entry.Key == update.Key), Is.False);

        await update.ApplyUpdateAsync(context, config).ConfigureAwait(false);
        await update.ApplyUpdateAsync(context, config).ConfigureAwait(false);

        var fixedPrice = spheres.First().PriceDefinition;
        Assert.That(fixedPrice?.BasePriceFormula, Is.EqualTo("value"));
        Assert.That(spheres.Select(item => item.Value), Is.EqualTo(TierPrices.SelectMany(price => Enumerable.Repeat(price, 6))));
        Assert.That(spheres.Select(item => item.PriceDefinition), Is.All.SameAs(fixedPrice));
        Assert.That(config.Items.Where(item => !IsSphere(item)).Select(item => (item.Value, item.PriceDefinition)), Is.EqualTo(controls));
        Assert.That(config.ItemPriceDefinitions, Is.EqualTo(priceDefinitions));
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Count(entry => entry.Key == update.Key), Is.EqualTo(1));
    }

    /// <summary>
    /// Verifies that a configuration without the shared fixed price can apply the update repeatedly.
    /// </summary>
    [Test]
    public async Task CreatesMissingFixedPriceOnExistingDatabaseAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        using var context = provider.CreateNewContext();
        var config = context.CreateNew<GameConfiguration>();
        var sphere = context.CreateNew<ItemDefinition>();
        sphere.Group = 12;
        sphere.Number = 129;
        config.Items.Add(sphere);
        var update = new SetMuExSeedSpherePricesUpdatePlugIn();

        await update.ApplyUpdateAsync(context, config).ConfigureAwait(false);
        await update.ApplyUpdateAsync(context, config).ConfigureAwait(false);

        Assert.That(config.ItemPriceDefinitions.Single(), Is.SameAs(sphere.PriceDefinition));
        Assert.That(sphere.Value, Is.EqualTo(1_350_000));
        Assert.That(sphere.PriceDefinition!.BasePriceFormula, Is.EqualTo("value"));
    }

    private static bool IsSphere(ItemDefinition item) => item is { Group: 12, Number: >= 100 and <= 129 };
}
