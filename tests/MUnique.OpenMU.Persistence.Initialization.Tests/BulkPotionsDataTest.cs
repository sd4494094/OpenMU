// <copyright file="BulkPotionsDataTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Persistence.Initialization.Updates;
using MUnique.OpenMU.Persistence.InMemory;

/// <summary>Checks fresh data and repeatable upgrades of the potion girl's bulk offers.</summary>
[TestFixture]
internal class BulkPotionsDataTest
{
    /// <summary>New databases contain both the existing packs and the additional bulk offers.</summary>
    [Test]
    public async Task NewDatabaseHasAllPackSizesAsync()
    {
        var (context, configuration) = await CreateConfigurationAsync().ConfigureAwait(false);
        using var _ = context;
        AssertOffers(configuration);
    }

    /// <summary>Upgrades repair old capacity and add missing offers only once.</summary>
    [Test]
    public async Task ExistingDatabaseUpgradePreservesSmallPacksAsync()
    {
        var (context, configuration) = await CreateConfigurationAsync().ConfigureAwait(false);
        using var _ = context;
        var store = configuration.Monsters.Single(monster => monster.Number == 253).MerchantStore!;
        foreach (var item in store.Items.Where(item => item.Durability == 100).ToArray())
        {
            store.Items.Remove(item);
        }

        var original = store.Items.ToArray();
        foreach (var definition in original.Where(item => item.Definition?.Group == 14 && item.Definition.Number is >= 0 and <= 6 or 8).Select(item => item.Definition!).Distinct())
        {
            definition.Durability = 3;
        }

        var update = new AddBulkPotionOffersPlugIn();
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);

        AssertOffers(configuration);
        Assert.That(original.All(store.Items.Contains), Is.True);
        Assert.That(store.Items.Count, Is.EqualTo(original.Length + 12));
    }

    private static void AssertOffers(GameConfiguration configuration)
    {
        var store = configuration.Monsters.Single(monster => monster.Number == 253).MerchantStore!;
        var bulk = store.Items.Where(item => item.Durability == 100).ToArray();
        Assert.That(bulk, Has.Length.EqualTo(12));
        Assert.That(bulk.Select(item => (item.Definition, item.Level)).Distinct().Count(), Is.EqualTo(12));
        foreach (var offer in bulk)
        {
            Assert.That(offer.Definition!.Durability, Is.GreaterThanOrEqualTo(100));
            Assert.That(store.Items.Any(item => item.Definition == offer.Definition && item.Level == offer.Level && item.Durability == 1), Is.True);
            Assert.That(store.Items.Any(item => item.Definition == offer.Definition && item.Level == offer.Level && item.Durability == 3), Is.True);
        }

        var cells = new HashSet<int>();
        foreach (var item in store.Items)
        {
            for (var y = 0; y < item.Definition!.Height; y++)
            {
                for (var x = 0; x < item.Definition.Width; x++)
                {
                    var slot = item.ItemSlot + (y * InventoryConstants.RowSize) + x;
                    Assert.That(slot, Is.LessThan(120));
                    Assert.That(cells.Add(slot), Is.True, $"Occupied shop cell {slot}");
                }
            }
        }
    }

    private static async Task<(IContext Context, GameConfiguration Configuration)> CreateConfigurationAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new VersionSeasonSix.DataInitialization(provider, NullLoggerFactory.Instance).CreateInitialDataAsync(1, true).ConfigureAwait(false);
        var context = provider.CreateNewContext();
        return (context, (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single());
    }
}
