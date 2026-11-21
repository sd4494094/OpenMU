// <copyright file="MuExElementalTalismansDataTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.Persistence.Initialization.Updates;

/// <summary>
/// Checks optional installation of the two elemental talisman definitions.
/// </summary>
[TestFixture]
internal class MuExElementalTalismansDataTest
{
    /// <summary>
    /// Checks fresh initialization, opt-in and repeat installation.
    /// </summary>
    /// <param name="preserveCustomDefinition">Whether one of the IDs has already been configured.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async Task AddsMissingDefinitionsWithoutChangingRecipesAsync(bool preserveCustomDefinition)
    {
        var provider = new InMemoryPersistenceContextProvider();
        var initializer = new VersionSeasonSix.DataInitialization(provider, NullLoggerFactory.Instance);
        await initializer.CreateInitialDataAsync(1, true).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var update = new AddMuExElementalTalismansUpdatePlugIn();
        Assert.That(update.IsMandatory, Is.False);
        Assert.That(update.RunsOnNewDatabase, Is.True);
        Assert.That(configuration.Items.Where(IsElementalTalisman), Is.Empty);
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Any(entry => entry.Key == update.Key), Is.False);

        ItemDefinition? custom = null;
        if (preserveCustomDefinition)
        {
            custom = context.CreateNew<ItemDefinition>();
            custom.Group = 14;
            custom.Number = 190;
            custom.Name = new LocalizedString("Custom protection talisman");
            custom.Width = 2;
            custom.Durability = 7;
            configuration.Items.Add(custom);
        }

        var existingRecipes = configuration.Monsters.SelectMany(monster => monster.ItemCraftings).ToArray();
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        var items = configuration.Items.Where(IsElementalTalisman).OrderBy(item => item.Number).ToArray();
        Assert.That(items.Select(item => item.Number), Is.EqualTo(new short[] { 189, 190 }));
        Assert.That(configuration.Monsters.SelectMany(monster => monster.ItemCraftings), Is.EqualTo(existingRecipes));
        var culture = CultureInfo.GetCultureInfo("zh-CN");
        Assert.That(items[0].Name.GetOwnTranslation(culture), Is.EqualTo("属性 幸运符咒"));
        Assert.That((items[0].Width, items[0].Height, items[0].Durability), Is.EqualTo(((byte)1, (byte)1, (byte)1)));
        Assert.That(items[0].DropsFromMonsters, Is.False);

        if (custom is null)
        {
            Assert.That(items[1].Name.GetOwnTranslation(culture), Is.EqualTo("属性 混沌合成符咒"));
            Assert.That((items[1].Width, items[1].Height, items[1].Durability), Is.EqualTo(((byte)1, (byte)1, (byte)1)));
            Assert.That(items[1].DropsFromMonsters, Is.False);
        }
        else
        {
            Assert.That(items[1], Is.SameAs(custom));
            Assert.That((items[1].Width, items[1].Durability), Is.EqualTo(((byte)2, (byte)7)));
        }

        items[0].Durability = 3;
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(configuration.Items.Where(IsElementalTalisman), Is.EquivalentTo(items));
        Assert.That(items[0].Durability, Is.EqualTo(3));
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Count(entry => entry.Key == update.Key), Is.EqualTo(1));
    }

    private static bool IsElementalTalisman(ItemDefinition item) => item.Group == 14 && item.Number is 189 or 190;
}
