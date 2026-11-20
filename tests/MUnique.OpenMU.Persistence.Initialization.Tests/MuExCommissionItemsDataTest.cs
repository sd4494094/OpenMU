// <copyright file="MuExCommissionItemsDataTest.cs" company="MUnique">
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
/// Verifies the optional Season 6 update for the MuEx level 2 commission items.
/// </summary>
[TestFixture]
internal class MuExCommissionItemsDataTest
{
    /// <summary>
    /// Verifies that a fresh database can opt in and that repeated application preserves existing items.
    /// </summary>
    /// <param name="customScrollExists">Whether the configuration has a custom definition for the scroll.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async Task ImportsPendingCommissionItemsAndPreservesOverridesAsync(bool customScrollExists)
    {
        var provider = new InMemoryPersistenceContextProvider();
        var initializer = new VersionSeasonSix.DataInitialization(provider, NullLoggerFactory.Instance);
        await initializer.CreateInitialDataAsync(1, true).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var update = new AddMuExCommissionItemsUpdatePlugIn();
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Any(entry => entry.Key == update.Key), Is.False);
        Assert.That(configuration.Items.Where(IsCommissionItem), Is.Empty);

        ItemDefinition? customScroll = null;
        if (customScrollExists)
        {
            customScroll = context.CreateNew<ItemDefinition>();
            customScroll.Group = 14;
            customScroll.Number = 171;
            customScroll.Name = new LocalizedString("Custom scroll");
            customScroll.Width = 2;
            customScroll.Height = 3;
            customScroll.Durability = 20;
            configuration.Items.Add(customScroll);
        }

        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        var items = configuration.Items.Where(IsCommissionItem).OrderBy(item => item.Number).ToArray();
        Assert.That(items.Select(item => (int)item.Number), Is.EqualTo(new[] { 170, 171 }));
        AssertDefinition(items[0], "Lv. 2 Letter of Request", "2级委托书");
        if (customScroll is null)
        {
            AssertDefinition(items[1], "Lv. 2 Completion Certificate", "2级委托完成卷轴");
        }
        else
        {
            Assert.That(items[1], Is.SameAs(customScroll));
            Assert.That(customScroll.Name.ValueInNeutralLanguage, Is.EqualTo("Custom scroll"));
            Assert.That(customScroll.Width, Is.EqualTo(2));
            Assert.That(customScroll.Height, Is.EqualTo(3));
            Assert.That(customScroll.Durability, Is.EqualTo(20));
        }

        items[0].Durability = 7;
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(configuration.Items.Where(IsCommissionItem), Is.EquivalentTo(items));
        Assert.That(items[0].Durability, Is.EqualTo(7));
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Count(entry => entry.Key == update.Key), Is.EqualTo(1));
    }

    private static bool IsCommissionItem(ItemDefinition item) => item.Group == 14 && item.Number is 170 or 171;

    private static void AssertDefinition(ItemDefinition item, string english, string chinese)
    {
        Assert.That(item.Width, Is.EqualTo(1));
        Assert.That(item.Height, Is.EqualTo(2));
        Assert.That(item.Durability, Is.EqualTo(1));
        Assert.That(item.DropsFromMonsters, Is.False);
        Assert.That(item.IsRepairable, Is.False);
        Assert.That(item.Name.ValueInNeutralLanguage, Is.EqualTo(english));
        Assert.That(item.Name.GetOwnTranslation(CultureInfo.GetCultureInfo("zh-CN")), Is.EqualTo(chinese));
    }
}
