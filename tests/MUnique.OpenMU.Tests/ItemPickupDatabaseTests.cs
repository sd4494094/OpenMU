// <copyright file="ItemPickupDatabaseTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.EntityFramework;
using MUnique.OpenMU.PlugIns;
using MUnique.OpenMU.Testing;
using StoredItem = MUnique.OpenMU.Persistence.EntityFramework.Model.Item;
using StoredOption = MUnique.OpenMU.Persistence.EntityFramework.Model.ItemOptionLink;

/// <summary>
/// Verifies actual PostgreSQL ownership transfers, stack consumption and transaction rejection.
/// </summary>
[TestFixture]
[Explicit("Requires OPENMU_INTEGRATION_TEST_CONNECTION pointing to a disposable PostgreSQL cluster.")]
[NonParallelizable]
public class ItemPickupDatabaseTests
{
    private PostgreSqlTestDatabase? _database;
    private PersistenceContextProvider _provider = null!;
    private GameConfiguration _configuration = null!;

    /// <summary>
    /// Creates and initializes one private database for the fixture.
    /// </summary>
    /// <returns>The task.</returns>
    [OneTimeSetUp]
    public async Task InitializeAsync()
    {
        this._database = await PostgreSqlTestDatabase.CreateAsync(initializeConfiguration: true).ConfigureAwait(false);
        this._provider = new PersistenceContextProvider(NullLoggerFactory.Instance, null);
        using var context = this._provider.CreateNewConfigurationContext();
        this._configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
    }

    /// <summary>
    /// Drops only the private database and role created for this fixture.
    /// </summary>
    /// <returns>The task.</returns>
    [OneTimeTearDown]
    public async Task CleanUpAsync()
    {
        if (this._database is not null)
        {
            await this._database.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A real rejected save cannot grant an inventory item or stack quantity; a successful
    /// transfer persists the original aggregate or deletes it with the target quantity.
    /// </summary>
    /// <param name="stack">Whether the source merges into an inventory stack.</param>
    /// <param name="reject">Whether the database rejects the source update or deletion.</param>
    /// <returns>The task.</returns>
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task PickupCommitsInventoryOutcomeAsync(bool stack, bool reject)
    {
        var login = $"pick{(stack ? 1 : 0)}{(reject ? 1 : 0)}";
        Guid sourceId;
        Guid? targetId = null;
        Guid? optionId = null;
        Item source;
        using (var context = this._provider.CreateNewPlayerContext(this._configuration))
        {
            var account = context.CreateNew<Account>();
            account.LoginName = login;
            account.PasswordHash = "unused-fixture-password";
            var character = context.CreateNew<Character>();
            character.Name = login;
            character.CharacterClass = this._configuration.CharacterClasses.First(c => c.CanGetCreated && c.HomeMap is not null);
            character.CurrentMap = character.CharacterClass.HomeMap;
            character.CreateDate = DateTime.UtcNow;
            character.KeyConfiguration = new byte[30];
            character.Inventory = context.CreateNew<ItemStorage>();
            character.Inventory.Money = 4321;
            foreach (var attribute in character.CharacterClass.StatAttributes)
            {
                character.Attributes.Add(context.CreateNew<StatAttribute>(attribute.Attribute, attribute.BaseValue));
            }

            account.Characters.Add(character);
            source = context.CreateNew<Item>();
            source.Definition = stack
                ? this._configuration.Items.First(d => d.Durability >= 5 && new Item { Definition = d }.IsStackable())
                : this._configuration.Items.First(d => d.Width == 2 && d.Height == 2 && d.PossibleItemOptions.Any(o => o.PossibleOptions.Any()));
            source.Durability = stack ? 2 : 1;
            sourceId = source.GetId();
            if (stack)
            {
                var target = context.CreateNew<Item>();
                target.Definition = source.Definition;
                target.Durability = 3;
                target.ItemSlot = 12;
                character.Inventory.Items.Add(target);
                targetId = target.GetId();
            }
            else
            {
                source.Level = 7;
                source.HasSkill = true;
                var option = context.CreateNew<ItemOptionLink>();
                option.ItemOption = source.Definition.PossibleItemOptions.SelectMany(o => o.PossibleOptions).First();
                option.Level = 1;
                source.ItemOptions.Add(option);
                optionId = option.GetId();
            }

            Assert.That(await context.SaveChangesAsync().ConfigureAwait(false), Is.True);
            context.Detach(source);
        }

        var initializer = new MapInitializer(this._configuration, NullLogger<MapInitializer>.Instance, NullDropGenerator.Instance, null);
        await using var game = new GameContext(this._configuration, this._provider, initializer, NullLoggerFactory.Instance,
            new PlugInManager(null, NullLoggerFactory.Instance, null, null), NullDropGenerator.Instance, new ConfigurationChangeMediator());
        initializer.PlugInManager = game.PlugInManager;
        initializer.PathFinderPool = game.PathFinderPool;
        game.StopPeriodicTasks();
        await using var player = new PickupPlayer(game);
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
        await using var drop = new DroppedItem(source, player.Position, player.CurrentMap!, player, player.GetAsEnumerable(), wasItemPersisted: true);
        await player.CurrentMap!.AddAsync(drop).ConfigureAwait(false);
        await using var schema = new EntityDataContext();
        if (reject)
        {
            // The identifier is generated by this fixture, never user-supplied SQL text.
            var gate = $$"""
                CREATE FUNCTION data.reject_pickup_fixture() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'REJECT_PICKUP_FIXTURE' USING ERRCODE='P0001'; END $$;
                CREATE TRIGGER reject_pickup_fixture BEFORE UPDATE OR DELETE ON data."Item"
                FOR EACH ROW WHEN (OLD."Id" = '{{sourceId:D}}'::uuid)
                EXECUTE FUNCTION data.reject_pickup_fixture();
                """;
            await schema.Database.ExecuteSqlRawAsync(gate).ConfigureAwait(false);
        }

        try
        {
            var result = await drop.TryPickUpByAsync(player).ConfigureAwait(false);
            Assert.That(result.Success, Is.EqualTo(!reject));
            if (reject)
            {
                Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.Finished));
                Assert.That((await drop.TryPickUpByAsync(player).ConfigureAwait(false)).Success, Is.False);
            }

            var persisted = await schema.Set<StoredItem>().AsNoTracking().SingleOrDefaultAsync(i => i.Id == sourceId).ConfigureAwait(false);
            var options = await schema.Set<StoredOption>().AsNoTracking().Where(o => EF.Property<Guid?>(o, "ItemId") == sourceId).ToListAsync().ConfigureAwait(false);
            if (stack)
            {
                var target = await schema.Set<StoredItem>().AsNoTracking().SingleAsync(i => i.Id == targetId).ConfigureAwait(false);
                Assert.That(target.Durability, Is.EqualTo(reject ? 3 : 5));
                Assert.That(persisted is null, Is.EqualTo(!reject));
            }
            else
            {
                Assert.That(persisted, Is.Not.Null);
                Assert.That(persisted!.ItemStorageId is null, Is.EqualTo(reject));
                Assert.That(persisted.Level, Is.EqualTo(7));
                Assert.That(persisted.HasSkill, Is.True);
                Assert.That(options.Single().Id, Is.EqualTo(optionId));
            }

            using var newContext = this._provider.CreateNewPlayerContext(this._configuration);
            var reloaded = (await newContext.GetAccountByLoginNameAsync(login).ConfigureAwait(false))!.Characters.Single();
            Assert.That(reloaded.Inventory!.Money, Is.EqualTo(4321));
            Assert.That(reloaded.Inventory.Items.Count, Is.EqualTo(stack || !reject ? 1 : 0));
            if (stack)
            {
                Assert.That(reloaded.Inventory.Items.Single().GetId(), Is.EqualTo(targetId));
                Assert.That(reloaded.Inventory.Items.Single().Durability, Is.EqualTo(reject ? 3 : 5));
            }
            else if (!reject)
            {
                Assert.That(reloaded.Inventory.Items.Single().GetId(), Is.EqualTo(sourceId));
                Assert.That(reloaded.Inventory.Items.Single().ItemOptions.Single().GetId(), Is.EqualTo(optionId));
            }
        }
        finally
        {
            if (reject)
            {
                await schema.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_pickup_fixture ON data.\"Item\"; DROP FUNCTION data.reject_pickup_fixture();").ConfigureAwait(false);
            }
        }
    }

    private sealed class PickupPlayer(IGameContext game) : Player(game)
    {
        protected override ICustomPlugInContainer<IViewPlugIn> CreateViewPlugInContainer() => new MockViewPlugInContainer();
    }
}
