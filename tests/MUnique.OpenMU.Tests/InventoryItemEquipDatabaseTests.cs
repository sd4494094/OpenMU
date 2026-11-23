// <copyright file="InventoryItemEquipDatabaseTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.GameLogic.Views.Inventory;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.EntityFramework;
using MUnique.OpenMU.PlugIns;
using MUnique.OpenMU.Testing;

/// <summary>
/// Checks persistence of the inventory item state marker in PostgreSQL.
/// </summary>
[TestFixture]
[Explicit("Requires OPENMU_INTEGRATION_TEST_CONNECTION pointing to a disposable PostgreSQL cluster.")]
[NonParallelizable]
public class InventoryItemEquipDatabaseTests
{
    /// <summary>
    /// Each acknowledged state is visible to a separate player context before logout.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task StateSurvivesNewPlayerContextAsync()
    {
        const string login = "equiptest";
        const byte slot = 75;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(initializeConfiguration: true).ConfigureAwait(false);
        var authoringProvider = new PersistenceContextProvider(NullLoggerFactory.Instance, null);
        using (var authoring = authoringProvider.CreateNewContext())
        {
            var authoredConfiguration = (await authoring.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
            var authoredItem = authoring.CreateNew<ItemDefinition>();
            authoredItem.Name = "Inventory state fixture";
            authoredItem.Group = 13;
            authoredItem.Number = 134;
            authoredItem.Width = authoredItem.Height = 1;
            authoredItem.Durability = 255;
            authoredConfiguration.Items.Add(authoredItem);
            Assert.That(await authoring.SaveChangesAsync().ConfigureAwait(false), Is.True);
        }

        var provider = new PersistenceContextProvider(NullLoggerFactory.Instance, null);
        using var configurationContext = provider.CreateNewConfigurationContext();
        var configuration = (await configurationContext.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var definition = configuration.Items.Single(item => item.Group == 13 && item.Number == 134);
        Guid itemId;
        using (var seed = provider.CreateNewPlayerContext(configuration))
        {
            var account = seed.CreateNew<Account>();
            account.LoginName = login;
            account.PasswordHash = "unused-fixture-password";
            var character = seed.CreateNew<Character>();
            character.Name = "EquipTest";
            character.CharacterClass = configuration.CharacterClasses.First(c => c.CanGetCreated && c.HomeMap is not null);
            character.CurrentMap = character.CharacterClass.HomeMap;
            character.CreateDate = DateTime.UtcNow;
            character.KeyConfiguration = new byte[30];
            character.Inventory = seed.CreateNew<ItemStorage>();
            account.Characters.Add(character);
            foreach (var attribute in character.CharacterClass.StatAttributes)
            {
                character.Attributes.Add(seed.CreateNew<StatAttribute>(attribute.Attribute, attribute.BaseValue));
            }

            var item = seed.CreateNew<Item>();
            item.Definition = definition;
            item.ItemSlot = slot;
            item.Durability = 255;
            character.Inventory.Items.Add(item);
            itemId = item.GetId();
            Assert.That(await seed.SaveChangesAsync().ConfigureAwait(false), Is.True);
        }

        var maps = new MapInitializer(configuration, NullLogger<MapInitializer>.Instance, NullDropGenerator.Instance, null);
        var manager = new PlugInManager(null, NullLoggerFactory.Instance, null, null);
        manager.RegisterPlugIn<IFeaturePlugIn, InventoryItemEquipFeaturePlugIn>();
        var feature = typeof(InventoryItemEquipFeaturePlugIn).GUID;
        manager.ReadConfigurations([new PlugInConfiguration
        {
            TypeId = feature,
            IsActive = true,
            CustomConfiguration = """{"13":[134]}""",
        }]);
        await using var game = new GameContext(configuration, provider, maps, NullLoggerFactory.Instance, manager,
            NullDropGenerator.Instance, new ConfigurationChangeMediator());
        maps.PlugInManager = manager;
        maps.PathFinderPool = game.PathFinderPool;
        game.StopPeriodicTasks();

        await using var player = new InventoryEquipPlayer(game);
        var loaded = await player.PersistenceContext.GetAccountByLoginNameAsync(login).ConfigureAwait(false);
        await player.SetAccountAsync(loaded!).ConfigureAwait(false);
        await player.PlayerState.TryAdvanceToAsync(PlayerState.LoginScreen).ConfigureAwait(false);
        await player.PlayerState.TryAdvanceToAsync(PlayerState.Authenticated).ConfigureAwait(false);
        await player.PlayerState.TryAdvanceToAsync(PlayerState.CharacterSelection).ConfigureAwait(false);
        await player.SetSelectedCharacterAsync(loaded!.Characters.Single()).ConfigureAwait(false);
        for (var ready = 0; ready < 3 && player.PlayerState.CurrentState == PlayerState.ChangingMap; ready++)
        {
            await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
        }

        Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.EnteredWorld));
        Assert.That(game.FeaturePlugIns.GetPlugIn<InventoryItemEquipFeaturePlugIn>()?.Configuration?[13], Is.EqualTo(new short[] { 134 }));

        var loadedItem = player.Inventory!.GetItem(slot);
        Assert.That(loadedItem, Is.Not.Null);
        Assert.That(loadedItem!.Definition, Is.Not.Null);
        Assert.That(loadedItem.Definition!.Group, Is.EqualTo(13));
        Assert.That(loadedItem.Definition.Number, Is.EqualTo(134));
        var action = new EquipInventoryItemAction();
        var view = Mock.Get(player.ViewPlugIns.GetPlugIn<IInventoryItemEquipResultPlugIn>()!);
        foreach (var state in new byte[] { 254, 255 })
        {
            await action.EquipAsync(player, slot, state).ConfigureAwait(false);
            Assert.That(loadedItem.Durability, Is.EqualTo(state), "The live item changed.");
            view.Verify(p => p.ShowInventoryItemEquipResultAsync(slot, state), Times.Once);
            using var independent = provider.CreateNewPlayerContext(configuration);
            var reloaded = await independent.GetAccountByLoginNameAsync(login).ConfigureAwait(false);
            var reloadedItem = reloaded!.Characters.Single().Inventory!.Items.Single();
            Assert.That(reloadedItem.GetId(), Is.EqualTo(itemId));
            Assert.That(reloadedItem.ItemSlot, Is.EqualTo(slot));
            Assert.That(reloadedItem.Durability, Is.EqualTo(state));
        }
    }

    private sealed class InventoryEquipPlayer(IGameContext game) : Player(game)
    {
        protected override ICustomPlugInContainer<IViewPlugIn> CreateViewPlugInContainer() => new MockViewPlugInContainer();
    }
}
