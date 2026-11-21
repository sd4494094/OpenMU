// <copyright file="MuExSeedSphereSaleTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.GameLogic.Views.Inventory;
using MUnique.OpenMU.Persistence.Initialization.Updates;
using MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix;
using MUnique.OpenMU.Persistence.InMemory;

/// <summary>
/// Verifies NPC sale settlement for the opt-in MuEx seed sphere catalog.
/// </summary>
[TestFixture]
public class MuExSeedSphereSaleTests
{
    private GameConfiguration _configuration = null!;

    /// <summary>
    /// Loads the actual Season 6 item data and applies the optional pricing update.
    /// </summary>
    [OneTimeSetUp]
    public async ValueTask InitializeAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new DataInitialization(provider, NullLoggerFactory.Instance).CreateInitialDataAsync(0, false).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        this._configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        await new SetMuExSeedSpherePricesUpdatePlugIn().ApplyUpdateAsync(context, this._configuration).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks the actual sale, item removal, and refusal when the proceeds do not fit.
    /// </summary>
    /// <param name="number">The seed sphere number in group 12.</param>
    /// <param name="price">The corresponding MuMain selling price.</param>
    /// <param name="atLimit">Whether the player's balance is at the maximum.</param>
    [TestCase(100, 350_000, false)]
    [TestCase(106, 375_000, false)]
    [TestCase(112, 400_000, false)]
    [TestCase(118, 425_000, false)]
    [TestCase(124, 450_000, false)]
    [TestCase(129, 450_000, true)]
    public async ValueTask SaleSettlesAtTheCatalogPriceAsync(short number, int price, bool atLimit)
    {
        const byte slot = 20;
        await using var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var merchant = this._configuration.Monsters.First(monster => monster.MerchantStore?.Items.Count > 0);
        player.OpenedNpc = new NonPlayerCharacter(null!, merchant, null!);
        player.GameContext.Configuration.MaximumInventoryMoney = this._configuration.MaximumInventoryMoney;
        player.Money = atLimit ? this._configuration.MaximumInventoryMoney : 1_000_000;
        var before = player.Money;
        var item = player.PersistenceContext.CreateNew<Item>();
        item.Definition = this._configuration.Items.Single(definition => definition.Group == 12 && definition.Number == number);
        item.Durability = item.Definition.Durability;
        await player.Inventory!.AddItemAsync(slot, item).ConfigureAwait(false);

        var sold = await new SellItemToNpcAction().SellItemAsync(player, slot).ConfigureAwait(false);

        Assert.That(sold, Is.EqualTo(!atLimit));
        Assert.That(player.Money, Is.EqualTo(atLimit ? before : before + price));
        Assert.That(player.Inventory.GetItem(slot), atLimit ? Is.SameAs(item) : Is.Null);
        Mock.Get(player.ViewPlugIns.GetPlugIn<IItemSoldToNpcPlugIn>()!).Verify(view => view.ItemSoldToNpcAsync(!atLimit), Times.Once);
    }
}
