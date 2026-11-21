// <copyright file="MuExBattlefieldSpoilsDataTest.cs" company="MUnique">
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
/// Verifies the optional MuEx Battlefield Spoils item definition.
/// </summary>
[TestFixture]
internal class MuExBattlefieldSpoilsDataTest
{
    /// <summary>
    /// Checks opt-in, names, and repeated installation on a fresh Season 6 configuration.
    /// </summary>
    [Test]
    public async Task InstallsSpoilsOnlyWhenSelectedAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        var initializer = new VersionSeasonSix.DataInitialization(provider, NullLoggerFactory.Instance);
        await initializer.CreateInitialDataAsync(1, false).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var update = new AddMuExBattlefieldSpoilsUpdatePlugIn();

        Assert.That(update.IsMandatory, Is.False);
        Assert.That(update.RunsOnNewDatabase, Is.True);
        Assert.That(configuration.Items.Any(IsSpoils), Is.False);
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Any(entry => entry.Key == update.Key), Is.False);

        var countBefore = configuration.Items.Count;
        var craftingBefore = configuration.Monsters.SelectMany(monster => monster.ItemCraftings).ToArray();
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);

        var definition = configuration.Items.Single(IsSpoils);
        Assert.That(configuration.Items.Count, Is.EqualTo(countBefore + 1));
        Assert.That(definition.Name.ValueInNeutralLanguage, Is.EqualTo("Battlefield Spoils"));
        Assert.That(definition.Name.GetOwnTranslation(CultureInfo.GetCultureInfo("zh-CN")), Is.EqualTo("战场战利品"));
        Assert.That((definition.Width, definition.Height, definition.Durability), Is.EqualTo(((byte)1, (byte)1, (byte)1)));
        Assert.That(definition.DropsFromMonsters, Is.False);
        Assert.That(definition.IsRepairable, Is.False);
        Assert.That(configuration.Monsters.SelectMany(monster => monster.ItemCraftings), Is.EqualTo(craftingBefore));
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Count(entry => entry.Key == update.Key), Is.EqualTo(1));
    }

    /// <summary>
    /// Keeps an existing definition with the same stable item number.
    /// </summary>
    [Test]
    public async Task PreservesExistingCustomDefinitionAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        using var context = provider.CreateNewContext();
        var configuration = context.CreateNew<GameConfiguration>();
        var custom = context.CreateNew<ItemDefinition>();
        custom.Group = 13;
        custom.Number = 147;
        custom.Durability = 9;
        configuration.Items.Add(custom);

        await new AddMuExBattlefieldSpoilsUpdatePlugIn().ApplyUpdateAsync(context, configuration).ConfigureAwait(false);

        Assert.That(configuration.Items.Single(), Is.SameAs(custom));
        Assert.That(custom.Durability, Is.EqualTo(9));
    }

    private static bool IsSpoils(ItemDefinition definition) => definition.Group == 13 && definition.Number == 147;
}
