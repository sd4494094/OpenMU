// <copyright file="CraftingTalismansTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.ItemCrafting;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions.Craftings;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.GameLogic.Views.NPC;
using MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;
using B = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Tests the Talisman of Luck and the Talisman of Chaos Assembly in the craftings.
/// </summary>
[TestFixture]
public class CraftingTalismansTest
{
    private GameConfiguration _gameConfiguration = null!;

    private IGameContext _gameContext = null!;

    /// <summary>
    /// Sets up the test environment by initializing the season 6 configuration data.
    /// </summary>
    [OneTimeSetUp]
    public async ValueTask SetupAsync()
    {
        var contextProvider = new InMemoryPersistenceContextProvider();
        await new DataInitialization(contextProvider, new NullLoggerFactory()).CreateInitialDataAsync(0, false).ConfigureAwait(false);
        this._gameConfiguration = (await contextProvider.CreateNewConfigurationContext().GetAsync<GameConfiguration>().ConfigureAwait(false)).First();

        var mapInitializer = new MapInitializer(this._gameConfiguration, new NullLogger<MapInitializer>(), NullDropGenerator.Instance, null);
        var gameContext = new GameContext(this._gameConfiguration, contextProvider, mapInitializer, new NullLoggerFactory(), new PlugInManager(null, new NullLoggerFactory(), null, null), NullDropGenerator.Instance, new ConfigurationChangeMediator());
        mapInitializer.PlugInManager = gameContext.PlugInManager;
        mapInitializer.PathFinderPool = gameContext.PathFinderPool;
        this._gameContext = gameContext;
    }

    /// <summary>
    /// Tests that the Talisman of Luck increases the success rate of the +10 item upgrade by its durability.
    /// </summary>
    /// <param name="talismanDurabilities">The durabilities of the talismans which are put in.</param>
    /// <returns>The success rate.</returns>
    [TestCase(new byte[0], ExpectedResult = 60)]
    [TestCase(new byte[] { 1 }, ExpectedResult = 61)]
    [TestCase(new byte[] { 5, 2 }, ExpectedResult = 67)]
    [TestCase(new byte[] { 10 }, ExpectedResult = 70)]
    public async ValueTask<int> TalismanOfLuckIncreasesRateAsync(byte[] talismanDurabilities)
    {
        var player = await this.CreatePlayerWithUpgradeItemsAsync(talismanDurabilities).ConfigureAwait(false);

        var result = this.GetUpgradeHandler().TryGetRequiredItems(player, out _, out var successRate);

        Assert.That(result, Is.Null);
        return successRate;
    }

    /// <summary>
    /// Tests that talismans of luck with a total durability above 10 are refused.
    /// </summary>
    [Test]
    public async ValueTask TooManyTalismansOfLuckAreRefusedAsync()
    {
        var player = await this.CreatePlayerWithUpgradeItemsAsync([10, 1]).ConfigureAwait(false);

        var result = this.GetUpgradeHandler().TryGetRequiredItems(player, out _, out _);

        Assert.That(result, Is.EqualTo(CraftingResult.TooManyItems));
    }

    /// <summary>
    /// Tests that the Talisman of Luck respects the maximum success rate of a crafting.
    /// </summary>
    [Test]
    public async ValueTask TalismanOfLuckRespectsMaximumRateAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        await AddItemAsync(player, this.TalismanOfLuck, 0, 10).ConfigureAwait(false);
        var settings = new Persistence.BasicModel.SimpleCraftingSettings { SuccessPercent = 85, MaximumSuccessPercent = 90 };
        settings.RequiredItems.Add(CreateTalismanRequirement(this.TalismanOfLuck, 10, 1, MixResult.Disappear));

        var result = new SimpleItemCraftingHandler(settings).TryGetRequiredItems(player, out _, out var successRate);

        Assert.That(result, Is.Null);
        Assert.That(successRate, Is.EqualTo(90));
    }

    /// <summary>
    /// Tests that the Talisman of Luck is added on top of a rate which is based on the price of the items,
    /// and that its own price doesn't count to that rate.
    /// </summary>
    [Test]
    public async ValueTask TalismanOfLuckAddsToPriceBasedRateAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        var item = await AddItemAsync(player, this._gameConfiguration.Items.First(i => i.Group == 0), 9, 20).ConfigureAwait(false);
        await AddItemAsync(player, this.TalismanOfLuck, 0, 5).ConfigureAwait(false);
        var price = new ItemPriceCalculator().CalculateFinalOldBuyingPrice(item);
        var settings = new Persistence.BasicModel.SimpleCraftingSettings { NpcPriceDivisor = (int)Math.Max(1, price / 50) };
        var requiredItem = new Persistence.BasicModel.ItemCraftingRequiredItem { MinimumAmount = 1, MaximumAmount = 1, MaximumItemLevel = 15 };
        requiredItem.PossibleItems.Add(item.Definition!);
        settings.RequiredItems.Add(requiredItem);
        settings.RequiredItems.Add(CreateTalismanRequirement(this.TalismanOfLuck, 10, 1, MixResult.Disappear));
        var expectedRate = Math.Min(100, (price / settings.NpcPriceDivisor) + 5);

        var result = new SimpleItemCraftingHandler(settings).TryGetRequiredItems(player, out _, out var successRate);

        Assert.That(result, Is.Null);
        Assert.That(successRate, Is.EqualTo(expectedRate));
    }

    /// <summary>
    /// Verifies that a price-based socket weapon recipe caps the base rate before adding luck.
    /// </summary>
    /// <param name="baseRate">The rate derived from the item value before the recipe limit.</param>
    /// <param name="recipeLimit">The maximum success percent configured for the recipe.</param>
    /// <param name="talismanDurability">The fixed luck bonus, where zero means no talisman.</param>
    /// <param name="expectedSocketRate">The expected socket weapon recipe rate.</param>
    /// <param name="expectedStandardRate">The expected standard recipe rate.</param>
    [TestCase(16, 40, 5, 21, 21)]
    [TestCase(60, 40, 0, 40, 40)]
    [TestCase(60, 40, 10, 50, 40)]
    [TestCase(100, 98, 10, 100, 98)]
    [TestCase(95, 0, 10, 100, 100)]
    public async ValueTask SocketWeaponCraftingCapsBaseRateBeforeLuckAsync(
        int baseRate, byte recipeLimit, byte talismanDurability, byte expectedSocketRate, byte expectedStandardRate)
    {
        const int priceDivisor = 360_000;
        await using var player = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        var equipment = new Persistence.BasicModel.ItemDefinition
        {
            Id = Guid.NewGuid(),
            Width = 1,
            Height = 1,
            PriceDefinition = new Persistence.BasicModel.ItemPriceDefinition { CraftingReferencePrice = baseRate * priceDivisor },
        };
        await AddItemAsync(player, equipment, 7, 1).ConfigureAwait(false);
        if (talismanDurability > 0)
        {
            await AddItemAsync(player, this.TalismanOfLuck, 0, talismanDurability).ConfigureAwait(false);
        }

        var settings = new Persistence.BasicModel.SimpleCraftingSettings
        {
            NpcPriceDivisor = priceDivisor,
            MaximumSuccessPercent = recipeLimit,
        };
        var requiredItem = new Persistence.BasicModel.ItemCraftingRequiredItem { MinimumAmount = 1, MaximumAmount = 1, MaximumItemLevel = 15 };
        requiredItem.PossibleItems.Add(equipment);
        settings.RequiredItems.Add(requiredItem);
        settings.RequiredItems.Add(CreateTalismanRequirement(this.TalismanOfLuck, 10, 1, MixResult.Disappear));

        var socketResult = new Level400SocketWeaponCrafting(settings).TryGetRequiredItems(player, out _, out var socketRate);
        var standardResult = new SimpleItemCraftingHandler(settings).TryGetRequiredItems(player, out _, out var standardRate);

        Assert.That(socketResult, Is.Null);
        Assert.That(socketRate, Is.EqualTo(expectedSocketRate));
        Assert.That(standardResult, Is.Null);
        Assert.That(standardRate, Is.EqualTo(expectedStandardRate));
    }

    /// <summary>
    /// Verifies the same-number monster wing recipes through the NPC preview.
    /// </summary>
    /// <param name="wingGroup">The item group of the wing or cape.</param>
    /// <param name="wingNumber">The item number of the wing or cape.</param>
    /// <param name="wingLevel">The level of the wing or cape.</param>
    /// <param name="catalystCount">The number of different monster materials supplied.</param>
    /// <param name="luck">The durability of the luck talisman, or zero if omitted.</param>
    /// <param name="expectedRate">The quoted rate, or -1 if there is no matching recipe.</param>
    [TestCase(12, 3, 0, 1, 0, 9)]
    [TestCase(12, 4, 0, 1, 0, 9)]
    [TestCase(12, 5, 0, 1, 0, 9)]
    [TestCase(12, 6, 15, 1, 10, 70)]
    [TestCase(12, 42, 0, 1, 3, 12)]
    [TestCase(12, 49, 0, 1, 0, 0)]
    [TestCase(12, 49, 0, 1, 5, 5)]
    [TestCase(13, 30, 0, 1, 0, 0)]
    [TestCase(13, 30, 0, 1, 10, 10)]
    [TestCase(12, 3, 0, 0, 0, -1)]
    [TestCase(12, 3, 0, 2, 0, -1)]
    [TestCase(12, 3, 0, 1, 11, -1)]
    [TestCase(12, 0, 0, 1, 0, -1)]
    public async ValueTask MonsterWingCraftingQuotesMatchingRecipeAsync(
        byte wingGroup, short wingNumber, byte wingLevel, int catalystCount, byte luck, int expectedRate)
    {
        const int pricePerPoint = 100_000;
        var items = this._gameConfiguration.Items;
        var wing = items.Single(i => i.Group == wingGroup && i.Number == wingNumber);
        var creation = items.Single(i => i.Group == 14 && i.Number == 22);
        var chaos = items.Single(i => i.Group == 12 && i.Number == 15);
        var catalysts = Enumerable.Range(176, 4).Select(number => new B.ItemDefinition
        {
            Id = Guid.NewGuid(),
            Group = 14,
            Number = (short)number,
            Width = 1,
            Height = 1,
            Durability = 1,
            PriceDefinition = new B.ItemPriceDefinition { BasePriceFormula = "900000000" },
        }).ToArray();

        ItemDefinition[][] wingVariants =
        [
            items.Where(i => i.Group == 12 && i.Number is >= 3 and <= 6).ToArray(),
            [items.Single(i => i.Group == 12 && i.Number == 42)],
            [items.Single(i => i.Group == 12 && i.Number == 49)],
            [items.Single(i => i.Group == 13 && i.Number == 30)],
        ];
        var npc = new B.MonsterDefinition { Number = 238, NpcWindow = NpcWindow.ChaosMachine };
        ItemCrafting? expectedCrafting = null;
        foreach (var allowedWings in wingVariants)
        {
            var settings = new B.SimpleCraftingSettings
            {
                NpcPriceDivisor = 9_000_000,
                MaximumSuccessPercent = 60,
                MoneyPerFinalSuccessPercentage = pricePerPoint,
            };
            foreach (var definitions in new IEnumerable<ItemDefinition>[] { allowedWings, catalysts, [creation], [chaos] })
            {
                var requirement = new B.ItemCraftingRequiredItem { MinimumAmount = 1, MaximumAmount = 1, MaximumItemLevel = byte.MaxValue };
                foreach (var definition in definitions)
                {
                    requirement.PossibleItems.Add(definition);
                }

                settings.RequiredItems.Add(requirement);
            }

            settings.RequiredItems.Add(CreateTalismanRequirement(this.TalismanOfLuck, 10, 1, MixResult.Disappear));
            var crafting = new B.ItemCrafting
            {
                Id = Guid.NewGuid(),
                Number = 56,
                ItemCraftingHandlerClassName = typeof(MonsterWingCrafting).FullName!,
                SimpleCraftingSettings = settings,
            };
            npc.ItemCraftings.Add(crafting);
            if (allowedWings.Contains(wing))
            {
                expectedCrafting = crafting;
            }
        }

        await using var player = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        player.OpenedNpc = new NonPlayerCharacter(null!, npc, null!);
        player.Inventory!.ItemStorage.Money = 10_000_000;
        await AddItemAsync(player, wing, wingLevel, wing.Durability).ConfigureAwait(false);
        await AddItemAsync(player, creation, 0, 1).ConfigureAwait(false);
        await AddItemAsync(player, chaos, 0, 1).ConfigureAwait(false);
        foreach (var catalyst in catalysts.Take(catalystCount))
        {
            await AddItemAsync(player, catalyst, 0, 1).ConfigureAwait(false);
        }

        if (luck > 0)
        {
            await AddItemAsync(player, this.TalismanOfLuck, 0, luck).ConfigureAwait(false);
        }

        var storage = player.TemporaryStorage!;
        var ingredients = storage.Items.ToArray();
        var action = new ItemCraftAction();
        var preview = Mock.Get(player.ViewPlugIns.GetPlugIn<IShowItemCraftingPreviewPlugIn>()!);
        var craftingByItems = action.FindAppropriateCraftingByItems(player);
        await action.ShowCraftingPreviewAsync(player).ConfigureAwait(false);
        if (expectedRate < 0)
        {
            Assert.That(craftingByItems, Is.Null);
            preview.Verify(p => p.ShowCraftingPreviewAsync(null, 0, 0), Times.Once);
        }
        else
        {
            Assert.That(expectedCrafting, Is.Not.Null);
            Assert.That(craftingByItems, Is.SameAs(expectedCrafting));
            var cost = expectedRate * pricePerPoint;
            var (error, _, rate, price) = new MonsterWingCrafting(expectedCrafting!.SimpleCraftingSettings!).GetRequiredItemsAndPrice(player);
            Assert.That(error, Is.Null);
            Assert.That(rate, Is.EqualTo(expectedRate));
            Assert.That(price, Is.EqualTo(cost));
            preview.Verify(p => p.ShowCraftingPreviewAsync(expectedCrafting, (byte)expectedRate, cost), Times.Once);
        }

        Assert.That(storage.Items, Is.EquivalentTo(ingredients));
        Assert.That(player.Money, Is.EqualTo(10_000_000));
    }

    /// <summary>
    /// Verifies socket material pricing for a +7 Sword Breaker with one ordinary additional option level.
    /// </summary>
    /// <param name="socketCount">The number of sockets on the weapon.</param>
    /// <param name="expectedValue">The corresponding value after rounding.</param>
    /// <param name="tiersByLink">Optional mounted sphere tiers, in slot order. A third link repeats slot zero.</param>
    [TestCase(0, 1_122_800)]
    [TestCase(1, 2_021_100)]
    [TestCase(2, 2_919_300)]
    [TestCase(3, 3_817_600)]
    [TestCase(1, 3_071_100, 1)]
    [TestCase(1, 3_146_100, 2)]
    [TestCase(1, 3_221_100, 3)]
    [TestCase(1, 3_296_100, 4)]
    [TestCase(1, 3_371_100, 5)]
    [TestCase(1, 3_071_100, 1, 5, 5)]
    [TestCase(2, 5_319_300, 1, 5)]
    public async ValueTask SocketWeaponCraftingValuesSocketsBeforeRoundingAsync(int socketCount, int expectedValue, params int[] tiersByLink)
    {
        await using var player = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        var definition = this._gameConfiguration.Items.Single(item => item.Group == 0 && item.Number == 27);
        var item = await AddItemAsync(player, definition, 7, definition.Durability).ConfigureAwait(false);
        var option = player.PersistenceContext.CreateNew<ItemOptionLink>();
        option.ItemOption = definition.PossibleItemOptions.SelectMany(options => options.PossibleOptions)
            .First(candidate => candidate.OptionType == ItemOptionTypes.Option);
        option.Level = 1;
        item.ItemOptions.Add(option);
        item.SocketCount = socketCount;

        var sphereOption = definition.PossibleItemOptions.SelectMany(options => options.PossibleOptions)
            .First(candidate => candidate.OptionType == ItemOptionTypes.SocketOption && candidate.SubOptionType == (int)SocketSubOptionType.Fire);
        for (var index = 0; index < tiersByLink.Length; index++)
        {
            var sphere = player.PersistenceContext.CreateNew<ItemOptionLink>();
            sphere.ItemOption = sphereOption;
            sphere.Index = index % 2;
            sphere.Level = tiersByLink[index];
            item.ItemOptions.Add(sphere);
        }

        var settings = new Persistence.BasicModel.SimpleCraftingSettings { NpcPriceDivisor = expectedValue };
        var requirement = new Persistence.BasicModel.ItemCraftingRequiredItem { MinimumAmount = 1, MaximumAmount = 1, MaximumItemLevel = 15 };
        requirement.PossibleItems.Add(definition);
        settings.RequiredItems.Add(requirement);

        var handler = new Level400SocketWeaponCrafting(settings);
        var result = handler.TryGetRequiredItems(player, out _, out var rate);
        Assert.That(result, Is.Null);
        Assert.That(rate, Is.EqualTo(1));

        // Raising the divisor by one tests the exact upper bound without duplicating the price calculation.
        settings.NpcPriceDivisor++;
        result = handler.TryGetRequiredItems(player, out _, out rate);
        Assert.That(result, Is.Null);
        Assert.That(rate, Is.Zero);

        settings.NpcPriceDivisor = 1_122_800;
        var standard = new SimpleItemCraftingHandler(settings);
        Assert.That(standard.TryGetRequiredItems(player, out _, out var standardRate), Is.Null);
        Assert.That(standardRate, Is.EqualTo(1));
        settings.NpcPriceDivisor++;
        Assert.That(standard.TryGetRequiredItems(player, out _, out standardRate), Is.Null);
        Assert.That(standardRate, Is.Zero);
    }

    /// <summary>
    /// Verifies custom sphere prices and defaults for a partially configured list.
    /// </summary>
    [Test]
    public void SocketWeaponCraftingUsesOptionalSphereValues()
    {
        var plugIn = new SocketWeaponCraftingFeaturePlugIn();
        Assert.That(plugIn.CreateDefaultConfig(), Is.TypeOf<SocketWeaponCraftingConfiguration>());
        Assert.That(PlugInManager.DiscoverPlugInTypes(typeof(Level400SocketWeaponCrafting).Assembly), Does.Contain(plugIn.GetType()));
        var config = new SocketWeaponCraftingConfiguration { MountedSeedSphereValues = new List<int> { 25_000, 0 } };
        Assert.That(SocketWeaponCraftingConfiguration.GetMountedSeedSphereValue(config, 1), Is.EqualTo(25_000));
        Assert.That(SocketWeaponCraftingConfiguration.GetMountedSeedSphereValue(config, 2), Is.Zero);
        Assert.That(SocketWeaponCraftingConfiguration.GetMountedSeedSphereValue(config, 3), Is.EqualTo(1_200_000));
    }

    /// <summary>
    /// Tests that the Talisman of Chaos Assembly keeps the item of a failed item upgrade, and is used up itself.
    /// </summary>
    [Test]
    public async ValueTask TalismanOfChaosAssemblyProtectsItemAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        var item = await AddItemAsync(player, this._gameConfiguration.Items.First(i => i.Group == 0), 9, 20).ConfigureAwait(false);
        var talisman = await AddItemAsync(player, this.TalismanOfChaosAssembly, 0, 1).ConfigureAwait(false);
        var settings = CreateFailingUpgradeSettings(item.Definition!);
        settings.RequiredItems.Add(CreateTalismanRequirement(this.TalismanOfChaosAssembly, 1, 0, MixResult.DisappearAndProtectReferencedItems));

        var (result, _) = await new SimpleItemCraftingHandler(settings).DoMixAsync(player, 0).ConfigureAwait(false);

        Assert.That(result, Is.EqualTo(CraftingResult.Failed));
        Assert.That(player.TemporaryStorage!.Items, Does.Contain(item));
        Assert.That(player.TemporaryStorage.Items, Does.Not.Contain(talisman));
        Assert.That(item.Level, Is.EqualTo(9));
    }

    /// <summary>
    /// Tests that the item of a failed item upgrade disappears without the Talisman of Chaos Assembly.
    /// </summary>
    [Test]
    public async ValueTask ItemDisappearsWithoutTalismanOfChaosAssemblyAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        var item = await AddItemAsync(player, this._gameConfiguration.Items.First(i => i.Group == 0), 9, 20).ConfigureAwait(false);
        var settings = CreateFailingUpgradeSettings(item.Definition!);
        settings.RequiredItems.Add(CreateTalismanRequirement(this.TalismanOfChaosAssembly, 1, 0, MixResult.DisappearAndProtectReferencedItems));

        var (result, _) = await new SimpleItemCraftingHandler(settings).DoMixAsync(player, 0).ConfigureAwait(false);

        Assert.That(result, Is.EqualTo(CraftingResult.Failed));
        Assert.That(player.TemporaryStorage!.Items, Does.Not.Contain(item));
    }

    private ItemDefinition TalismanOfLuck => this._gameConfiguration.Items.Single(i => i.Group == 14 && i.Number == 53);

    private ItemDefinition TalismanOfChaosAssembly => this._gameConfiguration.Items.Single(i => i.Group == 14 && i.Number == 96);

    private static SimpleCraftingSettings CreateFailingUpgradeSettings(ItemDefinition definition)
    {
        var settings = new Persistence.BasicModel.SimpleCraftingSettings { SuccessPercent = 0 };
        var requiredItem = new Persistence.BasicModel.ItemCraftingRequiredItem
        {
            MinimumAmount = 1,
            MaximumAmount = 1,
            MinimumItemLevel = 9,
            MaximumItemLevel = 9,
            Reference = 1,
            SuccessResult = MixResult.StaysAsIs,
            FailResult = MixResult.Disappear,
        };
        requiredItem.PossibleItems.Add(definition);
        settings.RequiredItems.Add(requiredItem);
        return settings;
    }

    private static ItemCraftingRequiredItem CreateTalismanRequirement(ItemDefinition talisman, byte maximumAmount, byte addPercentage, MixResult failResult)
    {
        var requirement = new Persistence.BasicModel.ItemCraftingRequiredItem
        {
            MinimumAmount = 0,
            MaximumAmount = maximumAmount,
            AddPercentage = addPercentage,
            SuccessResult = MixResult.Disappear,
            FailResult = failResult,
        };
        requirement.PossibleItems.Add(talisman);
        return requirement;
    }

    private static async ValueTask<Item> AddItemAsync(Player player, ItemDefinition definition, byte level, byte durability)
    {
        var item = player.PersistenceContext.CreateNew<Item>();
        item.Definition = definition;
        item.Level = level;
        item.Durability = durability;
        Assert.That(await player.TemporaryStorage!.AddItemAsync(item).ConfigureAwait(false), Is.True);
        return item;
    }

    private SimpleItemCraftingHandler GetUpgradeHandler()
    {
        var crafting = this._gameConfiguration.Monsters.First(m => m.NpcWindow == NpcWindow.ChaosMachine).ItemCraftings.Single(c => c.Number == 3);
        return new SimpleItemCraftingHandler(crafting.SimpleCraftingSettings!);
    }

    private async ValueTask<Player> CreatePlayerWithUpgradeItemsAsync(byte[] talismanDurabilities)
    {
        var player = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        await AddItemAsync(player, this._gameConfiguration.Items.First(i => i.Group == 0), 9, 20).ConfigureAwait(false);
        await AddItemAsync(player, this._gameConfiguration.Items.Single(i => i.Group == 12 && i.Number == 15), 0, 1).ConfigureAwait(false);
        await AddItemAsync(player, this._gameConfiguration.Items.Single(i => i.Group == 14 && i.Number == 13), 0, 1).ConfigureAwait(false);
        await AddItemAsync(player, this._gameConfiguration.Items.Single(i => i.Group == 14 && i.Number == 14), 0, 1).ConfigureAwait(false);
        foreach (var durability in talismanDurabilities)
        {
            await AddItemAsync(player, this.TalismanOfLuck, 0, durability).ConfigureAwait(false);
        }

        return player;
    }
}
