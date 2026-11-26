// <copyright file="CraftingRecoveryDatabaseTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.EntityFramework;
using MUnique.OpenMU.PlugIns;
using MUnique.OpenMU.Testing;

/// <summary>
/// Checks retained item ownership through the real account loader, saves, migration and new players.
/// </summary>
[TestFixture]
[Explicit("Requires OPENMU_INTEGRATION_TEST_CONNECTION pointing to a disposable PostgreSQL cluster.")]
[NonParallelizable]
public class CraftingRecoveryDatabaseTests
{
    /// <summary>
    /// Full-bag logout persists the original result. A fresh context recovers it after space
    /// is freed, and a further load keeps the same item, price and empty recovery storage.
    /// </summary>
    [Test]
    public async ValueTask FullBagResultSurvivesIndependentPlayerContextsAsync()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(initializeConfiguration: true).ConfigureAwait(false);
        var provider = new PersistenceContextProvider(NullLoggerFactory.Instance, null);
        GameConfiguration config;
        using (var context = provider.CreateNewConfigurationContext())
        {
            config = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        }

        Guid resultId;
        Guid characterId;
        using (var context = provider.CreateNewPlayerContext(config))
        {
            var account = context.CreateNew<Account>();
            account.LoginName = "recovery";
            account.PasswordHash = "hash";
            var character = context.CreateNew<Character>();
            character.Name = "Recover";
            character.CharacterClass = config.CharacterClasses.First(c => c is { CanGetCreated: true, HomeMap: not null });
            character.CurrentMap = character.CharacterClass.HomeMap;
            character.CreateDate = DateTime.UtcNow;
            character.KeyConfiguration = new byte[30];
            character.Inventory = context.CreateNew<ItemStorage>();
            character.Inventory.Money = 990;
            account.Characters.Add(character);
            characterId = character.GetId();
            Assert.That(await context.SaveChangesAsync().ConfigureAwait(false), Is.True);
        }

        // An existing character created before this migration has no recovery storage.
        // Round-trip the schema to the previous migration and back before exercising it.
        await using (var schema = new EntityDataContext())
        {
            var migrator = schema.GetService<IMigrator>();
            await migrator.MigrateAsync("20261103010412_AddMiniGameFreeTicketItem").ConfigureAwait(false);
            await migrator.MigrateAsync().ConfigureAwait(false);
        }

        var initializer = new MapInitializer(config, NullLogger<MapInitializer>.Instance, NullDropGenerator.Instance, null);
        await using var game = new GameContext(config, provider, initializer, NullLoggerFactory.Instance,
            new PlugInManager(null, NullLoggerFactory.Instance, null, null), NullDropGenerator.Instance, new ConfigurationChangeMediator());
        initializer.PlugInManager = game.PlugInManager;
        initializer.PathFinderPool = game.PathFinderPool;
        var fillers = new Dictionary<Guid, byte>();
        await using (var player = await EnterAsync(game).ConfigureAwait(false))
        {
            Assert.That(player.SelectedCharacter!.GetId(), Is.EqualTo(characterId));
            Assert.That(player.SelectedCharacter!.RecoveryStorage, Is.Null);
            var inventory = player.Inventory!;
            var definition = config.Items.Single(item => item.Group == 14 && item.Number == 13);
            foreach (var slot in inventory.FreeSlots.ToArray())
            {
                var filler = player.PersistenceContext.CreateNew<Item>();
                filler.Definition = definition;
                filler.Durability = 1;
                Assert.That(await inventory.AddItemAsync(slot, filler).ConfigureAwait(false), Is.True);
                fillers.Add(filler.GetId(), slot);
            }

            var result = player.PersistenceContext.CreateNew<Item>();
            result.Definition = config.Items.First(item => item.Width == 2 && item.Height == 2);
            result.Durability = 1;
            result.Level = 7;
            resultId = result.GetId();
            Assert.That(await player.TemporaryStorage!.AddItemAsync(0, result).ConfigureAwait(false), Is.True);
            await player.RemoveFromGameAsync().ConfigureAwait(false);
        }

        using (var context = provider.CreateNewPlayerContext(config))
        {
            var account = await context.GetAccountByLoginNameAsync("recovery").ConfigureAwait(false);
            var character = account!.Characters.Single();
            Assert.That(character.RecoveryStorage!.Items.Single().GetId(), Is.EqualTo(resultId));
            Assert.That(character.Inventory!.Money, Is.EqualTo(990));
            Assert.That(character.Inventory.Items.ToDictionary(item => item.GetId(), item => item.ItemSlot), Is.EquivalentTo(fillers));
        }

        // Refuse removal of recovery ownership while an unclaimed result remains.
        await using (var schema = new EntityDataContext())
        {
            Assert.That(async () => await schema.GetService<IMigrator>()
                .MigrateAsync("20261103010412_AddMiniGameFreeTicketItem").ConfigureAwait(false),
                Throws.Exception.With.Message.Contains("Recover retained character items before downgrading"));

            var retainedStorageId = await schema.Set<Persistence.EntityFramework.Model.Character>()
                .Where(character => character.Id == characterId).Select(character => character.RecoveryStorageId)
                .SingleAsync().ConfigureAwait(false);
            Assert.That(retainedStorageId, Is.Not.Null);
            Assert.That(await schema.Set<Persistence.EntityFramework.Model.Item>()
                .AnyAsync(item => item.Id == resultId && EF.Property<Guid?>(item, "ItemStorageId") == retainedStorageId)
                .ConfigureAwait(false), Is.True);

            // EF commits each migration separately. Later, empty migrations may
            // already have been reverted before the recovery guard rejected its
            // own downgrade. Restore those before using the current account loader.
            await schema.GetService<IMigrator>().MigrateAsync().ConfigureAwait(false);
        }

        await using (var player = await EnterAsync(game).ConfigureAwait(false))
        {
            Assert.That(player.SelectedCharacter!.RecoveryStorage!.Items.Single().GetId(), Is.EqualTo(resultId));
            var inventory = player.Inventory!;
            foreach (var slot in new byte[] { 12, 13, 20, 21 })
            {
                var item = inventory.Items.Single(item => item.ItemSlot == slot);
                await inventory.RemoveItemAsync(item).ConfigureAwait(false);
                await player.PersistenceContext.DeleteAsync(item).ConfigureAwait(false);
                fillers.Remove(item.GetId());
            }

            var npc = new NonPlayerCharacter(null!, config.Monsters.Single(monster => monster.Number == 238), null!);
            await new TalkNpcAction().TalkToNpcAsync(player, npc).ConfigureAwait(false);
            Assert.That(player.SelectedCharacter.RecoveryStorage.Items, Is.Empty);
            var result = inventory.Items.Single(item => item.GetId() == resultId);
            Assert.That(result.Level, Is.EqualTo(7));
            Assert.That(result.Durability, Is.EqualTo(1));
            Assert.That(result.ItemSlot, Is.EqualTo(12));
            await new CloseNpcDialogAction().CloseNpcDialogAsync(player).ConfigureAwait(false);
            await player.RemoveFromGameAsync().ConfigureAwait(false);
        }

        using (var context = provider.CreateNewPlayerContext(config))
        {
            var account = await context.GetAccountByLoginNameAsync("recovery").ConfigureAwait(false);
            var character = account!.Characters.Single();
            Assert.That(character.RecoveryStorage!.Items, Is.Empty);
            Assert.That(character.Inventory!.Items.Count(item => item.GetId() == resultId), Is.EqualTo(1));
            Assert.That(character.Inventory.Money, Is.EqualTo(990));
            Assert.That(character.Inventory.Items.Where(item => item.GetId() != resultId)
                .ToDictionary(item => item.GetId(), item => item.ItemSlot), Is.EquivalentTo(fillers));
        }
    }

    private static async ValueTask<RecoveryPlayer> EnterAsync(IGameContext game)
    {
        var player = new RecoveryPlayer(game);
        var account = await player.PersistenceContext.GetAccountByLoginNameAsync("recovery").ConfigureAwait(false);
        await player.SetAccountAsync(account!).ConfigureAwait(false);
        await player.PlayerState.TryAdvanceToAsync(PlayerState.LoginScreen).ConfigureAwait(false);
        await player.PlayerState.TryAdvanceToAsync(PlayerState.Authenticated).ConfigureAwait(false);
        await player.PlayerState.TryAdvanceToAsync(PlayerState.CharacterSelection).ConfigureAwait(false);
        await player.SetSelectedCharacterAsync(account!.Characters.Single()).ConfigureAwait(false);
        for (var ready = 0; ready < 3 && player.PlayerState.CurrentState == PlayerState.ChangingMap; ready++)
        {
            await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
        }

        Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.EnteredWorld));
        return player;
    }

    private sealed class RecoveryPlayer(IGameContext game) : Player(game)
    {
        protected override ICustomPlugInContainer<IViewPlugIn> CreateViewPlugInContainer() => new MockViewPlugInContainer();
    }
}
