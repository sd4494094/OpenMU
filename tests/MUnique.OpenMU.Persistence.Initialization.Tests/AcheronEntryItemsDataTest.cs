// <copyright file="AcheronEntryItemsDataTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.Persistence.Initialization.Updates;
using MUnique.OpenMU.Persistence.InMemory;

/// <summary>
/// Tests creation and upgrade of the Acheron entry item definitions.
/// </summary>
[TestFixture]
internal class AcheronEntryItemsDataTest
{
    /// <summary>
    /// Both a fresh configuration and an upgraded configuration contain exactly the two definitions.
    /// </summary>
    /// <param name="upgrade">Whether to exercise the upgrade path and replay the update.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async Task DefinesAcheronItemsAsync(bool upgrade)
    {
        var contextProvider = new InMemoryPersistenceContextProvider();
        var initialization = new VersionSeasonSix.DataInitialization(contextProvider, new NullLoggerFactory());
        await initialization.CreateInitialDataAsync(1, true).ConfigureAwait(false);
        using var context = contextProvider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        if (upgrade)
        {
            foreach (var oldItem in configuration.Items.Where(IsAcheronItem).ToArray())
            {
                configuration.Items.Remove(oldItem);
            }

            var update = new AddAcheronEntryItemsUpdatePlugIn();
            await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
            AssertItems(configuration);
            await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        }

        AssertItems(configuration);
    }

    private static bool IsAcheronItem(ItemDefinition item) => item is { Group: 13, Number: 145 or 146 };

    private static void AssertItems(GameConfiguration configuration)
    {
        var items = configuration.Items.Where(IsAcheronItem).OrderBy(item => item.Number).ToList();
        Assert.That(items.Select(item => item.Number), Is.EqualTo(new short[] { 145, 146 }));
        Assert.That(items.Select(item => item.Durability), Is.EqualTo(new byte[] { 5, 1 }));
        Assert.That(items.All(item => item.Width == 1 && item.Height == 1 && !item.DropsFromMonsters), Is.True);
        Assert.That(items.Select(item => item.Name.ValueInNeutralLanguage), Is.EqualTo(new[] { "Spirit Map Fragment", "Spirit Map" }));
    }
}
