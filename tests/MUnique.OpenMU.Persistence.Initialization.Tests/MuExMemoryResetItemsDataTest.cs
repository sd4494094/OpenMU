// <copyright file="MuExMemoryResetItemsDataTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.Persistence.Initialization.Updates;

/// <summary>
/// Verifies the optional Season 6 update for the MuEx memory reset items.
/// </summary>
[TestFixture]
internal class MuExMemoryResetItemsDataTest
{
    /// <summary>
    /// Verifies that the update stays pending after initialization and preserves existing definitions on application.
    /// </summary>
    /// <param name="preserveExisting">Whether the configuration already contains one custom definition.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async Task ImportsPendingItemsAndPreservesOverridesAsync(bool preserveExisting)
    {
        var provider = new InMemoryPersistenceContextProvider();
        var initializer = new VersionSeasonSix.DataInitialization(provider, NullLoggerFactory.Instance);
        await initializer.CreateInitialDataAsync(1, true).ConfigureAwait(false);

        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var update = new AddMuExMemoryResetItemsUpdatePlugIn();
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Any(entry => entry.Key == update.Key), Is.False);
        Assert.That(configuration.Items.Where(IsMemoryResetItem), Is.Empty);

        ItemDefinition? custom = null;
        if (preserveExisting)
        {
            custom = context.CreateNew<ItemDefinition>();
            custom.Group = 13;
            custom.Number = 159;
            custom.Width = 3;
            custom.Height = 3;
            custom.Durability = 20;
            configuration.Items.Add(custom);
        }

        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        var items = configuration.Items.Where(IsMemoryResetItem).OrderBy(item => item.Number).ToArray();
        Assert.That(items.Select(item => (int)item.Number), Is.EqualTo(Enumerable.Range(148, 12)));

        var chinese = CultureInfo.GetCultureInfo("zh-CN");
        string[] expectedNames = ["绿色墨水", "蓝色墨水", "红色墨水", "精神的羊皮纸", "绿色忘却卷轴", "蓝色忘却卷轴",
            "红色忘却卷轴", "灰色忘却卷轴", "封印的绿色忘却卷轴", "封印的蓝色忘却卷轴", "封印的红色忘却卷轴", "封印的灰色忘却卷轴"];
        var imported = preserveExisting ? items[..^1] : items;
        foreach (var (item, name) in imported.Zip(expectedNames))
        {
            Assert.That(item.Name.GetOwnTranslation(chinese), Is.EqualTo(name));
            Assert.That(item.Width, Is.EqualTo(item.Number <= 151 ? 1 : 2));
            Assert.That(item.Height, Is.EqualTo(2));
            Assert.That(item.Durability, Is.EqualTo(1));
            Assert.That(item.DropsFromMonsters, Is.False);
            Assert.That(item.IsRepairable, Is.False);
            Assert.That(item.IsTradable, Is.EqualTo(item.Number < 156));
        }

        items[0].Durability = 7;
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(configuration.Items.Where(IsMemoryResetItem), Is.EquivalentTo(items));
        Assert.That(items[0].Durability, Is.EqualTo(7));
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Count(entry => entry.Key == update.Key), Is.EqualTo(1));
        if (custom is not null)
        {
            Assert.That(items[^1], Is.SameAs(custom));
            Assert.That(custom.Width, Is.EqualTo(3));
            Assert.That(custom.Height, Is.EqualTo(3));
            Assert.That(custom.Durability, Is.EqualTo(20));
            Assert.That(custom.IsTradable, Is.True);
        }
    }

    private static bool IsMemoryResetItem(ItemDefinition item) => item.Group == 13 && item.Number is >= 148 and <= 159;
}
