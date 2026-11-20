// <copyright file="MuExWingTalismansDataTest.cs" company="MUnique">
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
/// Verifies that the MuEx wing and cape talismans are opt-in and preserve existing definitions.
/// </summary>
[TestFixture]
internal class MuExWingTalismansDataTest
{
    /// <summary>
    /// Checks an opt-in on a newly initialized configuration and on one with a preexisting definition.
    /// </summary>
    /// <param name="preserveExisting">Whether the configuration has a custom definition.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async Task ImportsPendingItemsWithoutChangingExistingDefinitionsAsync(bool preserveExisting)
    {
        var provider = new InMemoryPersistenceContextProvider();
        var initializer = new VersionSeasonSix.DataInitialization(provider, NullLoggerFactory.Instance);
        await initializer.CreateInitialDataAsync(1, true).ConfigureAwait(false);

        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var update = new AddMuExWingTalismansUpdatePlugIn();
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Any(entry => entry.Key == update.Key), Is.False);
        Assert.That(configuration.Items.Where(IsWingTalisman), Is.Empty);

        ItemDefinition? custom = null;
        if (preserveExisting)
        {
            custom = context.CreateNew<ItemDefinition>();
            custom.Group = 13;
            custom.Number = 92;
            custom.Width = 2;
            custom.Durability = 20;
            configuration.Items.Add(custom);
        }

        var npc = configuration.Monsters.Single(monster => monster.Number == 238);
        var originalCraftings = npc.ItemCraftings.ToArray();
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        var items = configuration.Items.Where(IsWingTalisman).OrderBy(item => item.Number).ToArray();
        Assert.That(items.Select(item => (int)item.Number), Is.EqualTo(Enumerable.Range(83, 10)));
        Assert.That(npc.ItemCraftings, Is.EquivalentTo(originalCraftings));

        var chinese = CultureInfo.GetCultureInfo("zh-CN");
        string[] names =
        [
            "恶魔之翼合成符咒", "天使之翼合成符咒", "精灵之翼合成符咒", "灾难之翼合成符咒", "王者披风合成符咒",
            "飞龙之翼合成符咒", "魔魂之翼合成符咒", "圣灵之翼合成符咒", "绝望之翼合成符咒", "暗黑之翼合成符咒",
        ];
        var imported = preserveExisting ? items[..^1] : items;
        foreach (var (item, name) in imported.Zip(names))
        {
            Assert.That(item.Name.GetOwnTranslation(chinese), Is.EqualTo(name));
            Assert.That((item.Width, item.Height, item.Durability), Is.EqualTo(((byte)1, (byte)1, (byte)1)));
            Assert.That(item.DropsFromMonsters || item.IsDroppable || item.IsSellableToNpc || item.IsRepairable, Is.False);
        }

        items[0].Durability = 3;
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(configuration.Items.Where(IsWingTalisman), Is.EquivalentTo(items));
        Assert.That(items[0].Durability, Is.EqualTo(3));
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Count(entry => entry.Key == update.Key), Is.EqualTo(1));

        if (custom is not null)
        {
            Assert.That(items[^1], Is.SameAs(custom));
            Assert.That(custom.Width, Is.EqualTo(2));
            Assert.That(custom.Durability, Is.EqualTo(20));
            Assert.That(custom.IsDroppable, Is.True);
        }
    }

    private static bool IsWingTalisman(ItemDefinition item) => item.Group == 13 && item.Number is >= 83 and <= 92;
}
