// <copyright file="MuExCraftingMaterialsDataTest.cs" company="MUnique">
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
/// Checks the optional MuEx material definitions.
/// </summary>
[TestFixture]
internal class MuExCraftingMaterialsDataTest
{
    private static readonly short[] ExpectedNumbers = [176, 177, 178, 179, 180, 181, 182, 183, 184, 185, 186, 187, 188, 191, 192, 193, 194, 195, 196, 197, 198, 199, 200, 201, 202];

    /// <summary>
    /// Checks that the update is pending in a fresh database, installs the materials, and preserves overrides on replay.
    /// </summary>
    [Test]
    public async Task ImportsPendingMaterialsAndPreservesOverridesAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        var initialize = new VersionSeasonSix.DataInitialization(provider, NullLoggerFactory.Instance);
        await initialize.CreateInitialDataAsync(1, true).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var update = new AddMuExCraftingMaterialsUpdatePlugIn();
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Any(entry => entry.Key == update.Key), Is.False);
        Assert.That(configuration.Items.Where(IsMuExMaterial), Is.Empty);

        var existing = context.CreateNew<ItemDefinition>();
        existing.Group = 14;
        existing.Number = 202;
        existing.Name = new LocalizedString("Custom material");
        existing.Width = 2;
        existing.Height = 3;
        existing.Durability = 20;
        configuration.Items.Add(existing);

        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        var imported = configuration.Items.Where(IsMuExMaterial).OrderBy(item => item.Number).ToArray();
        Assert.That(imported.Select(item => item.Number), Is.EqualTo(ExpectedNumbers));
        Assert.That(imported[^1], Is.SameAs(existing));
        var chinese = CultureInfo.GetCultureInfo("zh-CN");
        foreach (var material in imported[..^1])
        {
            Assert.That(material.Width, Is.EqualTo(1), $"Item {material.Number}");
            Assert.That(material.Height, Is.EqualTo(material.Number switch { 182 or 186 => 2, 188 => 4, _ => 1 }), $"Item {material.Number}");
            Assert.That(material.Durability, Is.EqualTo(1), $"Item {material.Number}");
            Assert.That(material.DropsFromMonsters, Is.False, $"Item {material.Number}");
            Assert.That(material.Name.GetOwnTranslation(chinese), Is.Not.Null.And.Not.Empty, $"Item {material.Number}");
        }

        Assert.That(imported[0].Name.GetOwnTranslation(chinese), Is.EqualTo("骷髅王骨头"));
        Assert.That(imported[0].Name.ValueInNeutralLanguage, Is.EqualTo("Skeleton King's Bone"));
        imported[0].Durability = 7;
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(configuration.Items.Where(IsMuExMaterial), Is.EquivalentTo(imported));
        Assert.That(imported[0].Durability, Is.EqualTo(7));
        Assert.That(existing.Name.ValueInNeutralLanguage, Is.EqualTo("Custom material"));
        Assert.That(existing.Width, Is.EqualTo(2));
        Assert.That(existing.Height, Is.EqualTo(3));
        Assert.That(existing.Durability, Is.EqualTo(20));
    }

    private static bool IsMuExMaterial(ItemDefinition item) => item.Group == 14 && ExpectedNumbers.Contains(item.Number);
}
