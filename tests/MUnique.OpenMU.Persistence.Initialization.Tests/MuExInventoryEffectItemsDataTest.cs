// <copyright file="MuExInventoryEffectItemsDataTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using System.Globalization;

using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.Persistence.Initialization.Updates;

/// <summary>
/// Verifies optional installation of the MuEx backpack effect item definitions.
/// </summary>
[TestFixture]
internal class MuExInventoryEffectItemsDataTest
{
    /// <summary>
    /// Checks the original item fields, opt-in installation and the unavailable stock cash shop offers.
    /// </summary>
    [Test]
    public async Task InstallsDefinitionsOnlyWhenSelectedAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        var initializer = new VersionSeasonSix.DataInitialization(provider, NullLoggerFactory.Instance);
        await initializer.CreateInitialDataAsync(1, false).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var update = new AddMuExInventoryEffectItemsUpdatePlugIn();

        Assert.That(update.IsMandatory, Is.False);
        Assert.That(update.RunsOnNewDatabase, Is.True);
        Assert.That(configuration.Items.Any(IsInventoryEffectItem), Is.False);
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Any(entry => entry.Key == update.Key), Is.False);
        var offers = configuration.CashShopConfiguration!.Packages.Where(package => package.PackageSequence is >= 348 and <= 357).ToArray();
        Assert.That(offers.Select(package => package.PackageSequence), Is.EquivalentTo(Enumerable.Range(348, 10)));
        Assert.That(offers.SelectMany(package => package.Products).Select(product => product.PriceSequence),
            Is.EquivalentTo(Enumerable.Range(528, 15).Concat(Enumerable.Range(528, 15))));
        Assert.That(offers.All(package => !package.IsForSale), Is.True);
        Assert.That(offers.SelectMany(package => package.Products).All(product => product.ItemDefinition is null), Is.True);

        var countBefore = configuration.Items.Count;
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);

        var definitions = configuration.Items.Where(IsInventoryEffectItem).OrderBy(item => item.Number).ToArray();
        Assert.That(configuration.Items.Count, Is.EqualTo(countBefore + 7));
        Assert.That(definitions.Select(item => item.Number), Is.EqualTo(Enumerable.Range(128, 7).Select(number => (short)number)));
        Assert.That(definitions.Select(item => item.Height), Is.EqualTo(new byte[] { 1, 1, 1, 1, 2, 2, 1 }));
        Assert.That(definitions.All(item => item.Width == 1 && item.Durability == byte.MaxValue && !item.IsRepairable && !item.DropsFromMonsters), Is.True);
        Assert.That(definitions.Select(item => item.GetId()).Distinct().Count(), Is.EqualTo(7));
        var chinese = CultureInfo.GetCultureInfo("zh-CN");
        Assert.That(definitions.Select(item => item.Name.GetOwnTranslation(chinese)),
            Is.EqualTo(new[] { "神鹰雕像", "山羊雕像", "兽人符文", "枫叶符文", "黄金兽人符文", "黄金枫叶符文", "破旧铁蹄" }));
        Assert.That(offers.All(package => !package.IsForSale), Is.True);
        Assert.That(offers.SelectMany(package => package.Products).All(product => product.ItemDefinition is null), Is.True);
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Count(entry => entry.Key == update.Key), Is.EqualTo(1));
    }

    /// <summary>
    /// Adds the missing definitions in an existing configuration and keeps custom items.
    /// </summary>
    [Test]
    public async Task AddsMissingDefinitionsAndPreservesCustomItemsAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        using var context = provider.CreateNewContext();
        var configuration = context.CreateNew<GameConfiguration>();
        var custom = context.CreateNew<ItemDefinition>();
        custom.Group = 13;
        custom.Number = 132;
        custom.Width = 2;
        custom.Height = 1;
        custom.Durability = 9;
        configuration.Items.Add(custom);

        await new AddMuExInventoryEffectItemsUpdatePlugIn().ApplyUpdateAsync(context, configuration).ConfigureAwait(false);

        Assert.That(configuration.Items.Count, Is.EqualTo(7));
        Assert.That(configuration.Items.Single(item => item.Number == 132), Is.SameAs(custom));
        Assert.That((custom.Width, custom.Height, custom.Durability), Is.EqualTo(((byte)2, (byte)1, (byte)9)));
    }

    private static bool IsInventoryEffectItem(ItemDefinition definition) => definition is { Group: 13, Number: >= 128 and <= 134 };
}
