// <copyright file="GroundItemDeliveryDatabaseTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.EntityFramework;
using MUnique.OpenMU.Testing;
using StoredDelivery = MUnique.OpenMU.Persistence.EntityFramework.Model.GroundItemDelivery;
using StoredItem = MUnique.OpenMU.Persistence.EntityFramework.Model.Item;
using StoredOption = MUnique.OpenMU.Persistence.EntityFramework.Model.ItemOptionLink;
using StoredStorage = MUnique.OpenMU.Persistence.EntityFramework.Model.ItemStorage;

/// <summary>
/// Verifies delivery generation conflicts and aggregate rollback on real PostgreSQL.
/// </summary>
/// <remarks>
/// These are persistence contract tests, not gameplay, deadline or recovery tests.
/// Both contenders load the original aggregate before either one commits.
/// </remarks>
[TestFixture]
[Explicit("Requires OPENMU_INTEGRATION_TEST_CONNECTION pointing to a disposable PostgreSQL cluster.")]
[NonParallelizable]
public class GroundItemDeliveryDatabaseTests
{
    private PostgreSqlTestDatabase? _database;
    private PersistenceContextProvider _provider = null!;
    private GameConfiguration _configuration = null!;

    /// <summary>
    /// Creates a private migrated and initialized database.
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
    /// Removes the fixture's private database and role.
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
    /// Pickup, stacking and expiry compete for one generation, with all losing
    /// inventory changes rolled back even when the same tracked save is retried.
    /// </summary>
    /// <param name="firstOperation">Pickup, stack or expiry for the first contender.</param>
    /// <param name="secondOperation">Pickup, stack or expiry for the second contender.</param>
    /// <param name="race">Whether to submit both commits concurrently.</param>
    /// <param name="firstUsesTrade">Whether the first contender uses the trade context.</param>
    /// <returns>The task.</returns>
    [Test]
    public async Task OnlyOneGenerationConsumerCommitsAsync(
        [Values("pickup", "stack", "expiry")] string firstOperation,
        [Values("pickup", "stack", "expiry")] string secondOperation,
        [Values(false, true)] bool race,
        [Values(false, true)] bool firstUsesTrade)
    {
        var fixture = await this.CreateDeliveryAsync().ConfigureAwait(false);
        using var first = this.CreateContender(firstUsesTrade);
        using var second = this.CreateContender(!firstUsesTrade);
        var firstSource = await LoadSourceAsync(first, fixture.SourceId).ConfigureAwait(false);
        var secondSource = await LoadSourceAsync(second, fixture.SourceId).ConfigureAwait(false);
        Assert.That(firstSource, Is.Not.SameAs(secondSource));
        Assert.That(firstSource.RawGroundDelivery, Is.Not.SameAs(secondSource.RawGroundDelivery));
        Assert.That(firstSource.GroundDeliveryId, Is.EqualTo(fixture.DeliveryId));
        Assert.That(secondSource.GroundDeliveryId, Is.EqualTo(fixture.DeliveryId));
        Assert.That(firstSource.RawGroundDelivery.ExpiresAt, Is.EqualTo(fixture.ExpiresAt));
        Assert.That(firstSource.RawGroundDelivery.HostServerId, Is.EqualTo(fixture.HostId));
        Assert.That(firstSource.RawGroundDelivery.MapDefinitionId, Is.EqualTo(fixture.MapId));
        Assert.That(firstSource.RawGroundDelivery.MapInstanceId, Is.EqualTo(fixture.InstanceId));
        Assert.That(firstSource.RawGroundDelivery.OwnerCharacterId, Is.EqualTo(fixture.OwnerId));
        Assert.That(firstSource.RawGroundDelivery.PositionX, Is.EqualTo(123));
        Assert.That(firstSource.RawGroundDelivery.PositionY, Is.EqualTo(45));
        Assert.That(firstSource.RawGroundDelivery.DroppedAt, Is.EqualTo(fixture.ExpiresAt.AddMinutes(-5)));
        Assert.That(firstSource.RawGroundDelivery.OwnerPriorityUntil, Is.EqualTo(fixture.ExpiresAt.AddMinutes(-5).AddSeconds(10)));

        await StageAsync(first, firstSource, fixture.FirstStorageId, fixture.FirstTargetId, firstOperation).ConfigureAwait(false);
        await StageAsync(second, secondSource, fixture.SecondStorageId, fixture.SecondTargetId, secondOperation).ConfigureAwait(false);

        bool firstWon;
        bool secondWon;
        if (race)
        {
            var commits = await Task.WhenAll(TrySaveAsync(first), TrySaveAsync(second)).ConfigureAwait(false);
            firstWon = commits[0];
            secondWon = commits[1];
        }
        else
        {
            firstWon = await TrySaveAsync(first).ConfigureAwait(false);
            secondWon = await TrySaveAsync(second).ConfigureAwait(false);
            Assert.That(firstWon, Is.True, "The first uncontested commit must succeed.");
        }

        Assert.That(new[] { firstWon, secondWon }.Count(won => won), Is.EqualTo(1));
        var loser = firstWon ? second : first;
        var loserSource = firstWon ? secondSource : firstSource;
        var winnerOperation = firstWon ? firstOperation : secondOperation;
        var winnerStorageId = firstWon ? fixture.FirstStorageId : fixture.SecondStorageId;

        // Teardown must retry this very tracker state. Refreshing or reattaching the
        // cleared item would replace M by null and could let a loser steal the item.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.That(loser.Context.Entry(loserSource).Property(item => item.GroundDeliveryId).OriginalValue,
                Is.EqualTo(fixture.DeliveryId));
            Assert.That(await TrySaveAsync(loser).ConfigureAwait(false), Is.False);
        }

        await using var verify = new EntityDataContext();
        var storedSource = await verify.Set<StoredItem>().SingleOrDefaultAsync(item => item.Id == fixture.SourceId).ConfigureAwait(false);
        if (winnerOperation == "pickup")
        {
            Assert.That(storedSource, Is.Not.Null);
            Assert.That(storedSource!.GroundDeliveryId, Is.Null);
            Assert.That(verify.Entry(storedSource).Property<Guid?>("ItemStorageId").CurrentValue, Is.EqualTo(winnerStorageId));
            Assert.That(storedSource.Level, Is.EqualTo(7));
            Assert.That(storedSource.HasSkill, Is.True);
            Assert.That(storedSource.Durability, Is.EqualTo(2));
            var option = await verify.Set<StoredOption>().SingleAsync(option => option.Id == fixture.OptionId).ConfigureAwait(false);
            Assert.That(verify.Entry(option).Property("ItemId").CurrentValue, Is.EqualTo(fixture.SourceId));
            Assert.That(option.ItemOptionId, Is.EqualTo(fixture.OptionDefinitionId));
            Assert.That(option.Level, Is.EqualTo(1));
            Assert.That(option.Index, Is.EqualTo(2));
            Assert.That(await verify.Set<StoredOption>().CountAsync(value => EF.Property<Guid?>(value, "ItemId") == fixture.SourceId).ConfigureAwait(false), Is.EqualTo(1));
        }
        else
        {
            Assert.That(storedSource, Is.Null);
            Assert.That(await verify.Set<StoredOption>().AnyAsync(option => option.Id == fixture.OptionId).ConfigureAwait(false), Is.False);
        }

        Assert.That(await verify.Set<StoredDelivery>().AnyAsync(delivery => delivery.Id == fixture.DeliveryId).ConfigureAwait(false), Is.False);
        var firstTarget = await verify.Set<StoredItem>().SingleAsync(item => item.Id == fixture.FirstTargetId).ConfigureAwait(false);
        var secondTarget = await verify.Set<StoredItem>().SingleAsync(item => item.Id == fixture.SecondTargetId).ConfigureAwait(false);
        Assert.That(firstTarget.Durability, Is.EqualTo(firstWon && firstOperation == "stack" ? 5 : 3));
        Assert.That(secondTarget.Durability, Is.EqualTo(secondWon && secondOperation == "stack" ? 5 : 3));
        Assert.That(await verify.Set<StoredStorage>().Where(storage => storage.Id == fixture.FirstStorageId || storage.Id == fixture.SecondStorageId)
            .Select(storage => storage.Money).ToListAsync().ConfigureAwait(false), Is.EquivalentTo(new[] { 4321, 4321 }));
    }

    /// <summary>
    /// The item token itself rejects a stale update or delete even when neither
    /// contender deletes the marker or shares a tracked option row.
    /// </summary>
    /// <param name="delete">Whether the stale contender deletes rather than transfers the source.</param>
    /// <param name="firstUsesTrade">Whether the winning contender uses the trade context.</param>
    /// <param name="redeliver">Whether the winner stages a new generation instead of clearing the original one.</param>
    /// <returns>The task.</returns>
    [Test]
    public async Task ItemTokenAloneRejectsStaleConsumerAsync([Values(false, true)] bool delete, [Values(false, true)] bool firstUsesTrade, [Values(false, true)] bool redeliver)
    {
        var fixture = await this.CreateDeliveryAsync().ConfigureAwait(false);
        using var winner = this.CreateContender(firstUsesTrade);
        using var loser = this.CreateContender(!firstUsesTrade);
        var original = await winner.Context.Set<StoredItem>().SingleAsync(item => item.Id == fixture.SourceId).ConfigureAwait(false);
        var stale = await loser.Context.Set<StoredItem>().SingleAsync(item => item.Id == fixture.SourceId).ConfigureAwait(false);
        Assert.That(winner.Context.ChangeTracker.Entries<StoredDelivery>(), Is.Empty);
        Assert.That(loser.Context.ChangeTracker.Entries<StoredDelivery>(), Is.Empty);
        Assert.That(loser.Context.ChangeTracker.Entries<StoredOption>(), Is.Empty);

        // Leave the marker untouched deliberately: its DELETE cannot mask an absent
        // Item token. This fixture models the contested row predicate, not cleanup.
        Guid? nextGeneration = null;
        if (redeliver)
        {
            var next = winner.CreateNew<GroundItemDelivery>();
            next.HostServerId = fixture.HostId;
            next.MapDefinitionId = fixture.MapId;
            next.OwnerCharacterId = fixture.OwnerId;
            next.DroppedAt = fixture.ExpiresAt;
            next.OwnerPriorityUntil = fixture.ExpiresAt.AddSeconds(10);
            next.ExpiresAt = fixture.ExpiresAt.AddMinutes(5);
            original.GroundDelivery = next;
            nextGeneration = next.GetId();
            Assert.That(nextGeneration, Is.Not.EqualTo(fixture.DeliveryId));
        }
        else
        {
            original.GroundDeliveryId = null;
        }
        winner.Context.Entry(original).Property<Guid?>("ItemStorageId").CurrentValue = fixture.FirstStorageId;
        Assert.That(await winner.SaveChangesAsync().ConfigureAwait(false), Is.True);
        if (delete)
        {
            loser.Context.Remove(stale);
        }
        else
        {
            stale.GroundDeliveryId = null;
            loser.Context.Entry(stale).Property<Guid?>("ItemStorageId").CurrentValue = fixture.SecondStorageId;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.That(loser.Context.Entry(stale).Property(item => item.GroundDeliveryId).OriginalValue, Is.EqualTo(fixture.DeliveryId));
            var rejection = Assert.ThrowsAsync<PersistenceConflictException>(async () =>
                await loser.SaveChangesAsync().ConfigureAwait(false));
            Assert.That(rejection!.InnerException, Is.TypeOf<DbUpdateConcurrencyException>());
            Assert.That(((DbUpdateConcurrencyException)rejection.InnerException!).Entries.Select(entry => entry.Entity), Is.EqualTo(new[] { stale }));
        }

        await using var verify = new EntityDataContext();
        var item = await verify.Set<StoredItem>().SingleAsync(value => value.Id == fixture.SourceId).ConfigureAwait(false);
        Assert.That(item.GroundDeliveryId, Is.EqualTo(nextGeneration));
        Assert.That(verify.Entry(item).Property<Guid?>("ItemStorageId").CurrentValue, Is.EqualTo(fixture.FirstStorageId));
        Assert.That(await verify.Set<StoredDelivery>().AnyAsync(marker => marker.Id == fixture.DeliveryId).ConfigureAwait(false), Is.True);
        Assert.That(await verify.Set<StoredOption>().AnyAsync(option => option.Id == fixture.OptionId).ConfigureAwait(false), Is.True);
    }

    /// <summary>
    /// A recovery page preserves the aggregate and uses the actual cached option and set
    /// definitions. Its detached result can enter another context without losing token M.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task RecoveryPageIsDetachedAndCanBeConsumedAsync()
    {
        var fixture = await this.CreateDeliveryAsync(includeSet: true).ConfigureAwait(false);
        Item source;
        using (var reader = this._provider.CreateNewPlayerContext(this._configuration))
        {
            source = (await reader.GetGroundItemPageAsync(QueryFor(fixture)).ConfigureAwait(false)).Single();
            Assert.That(GetDatabaseContext(reader).ChangeTracker.Entries(), Is.Empty);
            Assert.That(source.GetId(), Is.EqualTo(fixture.SourceId));
            Assert.That(source.GroundDelivery!.GetId(), Is.EqualTo(fixture.DeliveryId));
            Assert.That(source.GroundDelivery!.ExpiresAt, Is.EqualTo(fixture.ExpiresAt));
            Assert.That(source.GroundDelivery!.ExpiresAt.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(source.GroundDelivery!.DroppedAt, Is.EqualTo(fixture.ExpiresAt.AddMinutes(-5)));
            Assert.That(source.GroundDelivery!.DroppedAt.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(source.GroundDelivery!.OwnerPriorityUntil, Is.EqualTo(fixture.ExpiresAt.AddMinutes(-5).AddSeconds(10)));
            Assert.That(source.GroundDelivery!.OwnerPriorityUntil.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(source.GroundDelivery!.MapInstanceId, Is.EqualTo(fixture.InstanceId));
            Assert.That(source.Level, Is.EqualTo(7));
            Assert.That(source.HasSkill, Is.True);
            Assert.That(source.Durability, Is.EqualTo(2));
            Assert.That(source.Definition, Is.SameAs(this._configuration.Items.Single(value => value.GetId() == source.Definition!.GetId())));
            var option = source.ItemOptions.Single();
            Assert.That(option.GetId(), Is.EqualTo(fixture.OptionId));
            Assert.That(option.ItemOption, Is.SameAs(this._configuration.ItemOptions.SelectMany(value => value.PossibleOptions)
                .Single(value => value.GetId() == fixture.OptionDefinitionId)));
            Assert.That(option.Level, Is.EqualTo(1));
            Assert.That(option.Index, Is.EqualTo(2));
            Assert.That(source.ItemSetGroups.Single(), Is.SameAs(this._configuration.ItemSetGroups.SelectMany(value => value.Items)
                .Single(value => value.GetId() == fixture.SetId)));
        }

        using (var consumer = this._provider.CreateNewPlayerContext(this._configuration))
        {
            consumer.Attach(source);
            var database = GetDatabaseContext(consumer);
            var stored = (StoredItem)source;
            Assert.That(database.Entry(stored).Property(item => item.GroundDeliveryId).OriginalValue, Is.EqualTo(fixture.DeliveryId));
            var marker = source.GroundDelivery!;
            source.GroundDelivery = null;
            database.Entry(stored).Property<Guid?>("ItemStorageId").CurrentValue = fixture.FirstStorageId;
            Assert.That(await consumer.DeleteAsync(marker).ConfigureAwait(false), Is.True);
            Assert.That(await consumer.SaveChangesAsync().ConfigureAwait(false), Is.True);
        }

        using var fresh = this._provider.CreateNewPlayerContext(this._configuration);
        Assert.That(await fresh.GetGroundItemPageAsync(QueryFor(fixture)).ConfigureAwait(false), Is.Empty);
        await using var verify = new EntityDataContext();
        var original = await verify.Set<StoredItem>().Include(item => item.RawItemOptions).Include(item => item.JoinedItemSetGroups)
            .SingleAsync(item => item.Id == fixture.SourceId).ConfigureAwait(false);
        Assert.That(verify.Entry(original).Property<Guid?>("ItemStorageId").CurrentValue, Is.EqualTo(fixture.FirstStorageId));
        Assert.That(original.RawItemOptions.Single().Id, Is.EqualTo(fixture.OptionId));
        Assert.That(original.JoinedItemSetGroups.Single().ItemOfItemSetId, Is.EqualTo(fixture.SetId));
        Assert.That(await verify.Set<StoredDelivery>().AnyAsync(marker => marker.Id == fixture.DeliveryId).ConfigureAwait(false), Is.False);
    }

    /// <summary>
    /// Host, map, exact instance and original expiry bounds cannot fall back to unrelated drops.
    /// </summary>
    /// <param name="filter">The mismatching page filter.</param>
    /// <returns>The task.</returns>
    [TestCase("host")]
    [TestCase("map")]
    [TestCase("instance")]
    [TestCase("ordinary")]
    [TestCase("lower")]
    [TestCase("upper")]
    public async Task RecoveryPageHonorsEveryFilterAsync(string filter)
    {
        var fixture = await this.CreateDeliveryAsync().ConfigureAwait(false);
        using var reader = this._provider.CreateNewPlayerContext(this._configuration);
        Assert.That(await reader.GetGroundItemPageAsync(QueryFor(fixture)).ConfigureAwait(false), Has.Count.EqualTo(1));
        var mismatch = new GroundItemQuery
        {
            HostServerId = filter == "host" ? Guid.NewGuid() : fixture.HostId,
            MapDefinitionId = filter == "map" ? Guid.NewGuid() : fixture.MapId,
            MapInstanceId = filter == "instance" ? Guid.NewGuid() : filter == "ordinary" ? null : fixture.InstanceId,
            ExpiresAfterUtc = filter == "lower" ? fixture.ExpiresAt : null,
            ExpiresBeforeUtc = filter == "upper" ? fixture.ExpiresAt.AddSeconds(-1) : null,
        };
        Assert.That(await reader.GetGroundItemPageAsync(mismatch).ConfigureAwait(false), Is.Empty);
        Assert.That(await reader.GetGroundItemPageAsync(new GroundItemQuery
        {
            HostServerId = fixture.HostId,
            IncludeAllInstances = true,
            ExpiresBeforeUtc = fixture.ExpiresAt,
        }).ConfigureAwait(false), Has.Count.EqualTo(1));
        Assert.That(GetDatabaseContext(reader).ChangeTracker.Entries(), Is.Empty);
    }

    /// <summary>
    /// Equal expiry timestamps use a second cursor field, and consuming a previous row
    /// between pages cannot shift an offset and skip another outstanding delivery.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task RecoveryPageCursorSurvivesEarlierConsumptionAsync()
    {
        var fixtures = new List<DeliveryFixture>();
        for (var index = 0; index < 5; index++)
        {
            fixtures.Add(await this.CreateDeliveryAsync().ConfigureAwait(false));
        }

        var root = fixtures[0];
        await using (var prepare = new EntityDataContext())
        {
            for (var index = 0; index < fixtures.Count; index++)
            {
                var fixture = fixtures[index];
                await prepare.Set<StoredDelivery>().Where(marker => marker.Id == fixture.DeliveryId).ExecuteUpdateAsync(update => update
                    .SetProperty(marker => marker.HostServerId, root.HostId)
                    .SetProperty(marker => marker.MapInstanceId, root.InstanceId)
                    .SetProperty(marker => marker.ExpiresAt, root.ExpiresAt.AddSeconds(index / 2))).ConfigureAwait(false);
            }
        }

        using var reader = this._provider.CreateNewPlayerContext(this._configuration);
        var seen = new List<Guid>();
        DateTime? afterTime = null;
        Guid? afterId = null;
        while (true)
        {
            var page = await reader.GetGroundItemPageAsync(new GroundItemQuery
            {
                HostServerId = root.HostId,
                MapDefinitionId = root.MapId,
                MapInstanceId = root.InstanceId,
                AfterExpiresAtUtc = afterTime,
                AfterDeliveryId = afterId,
                Limit = 2,
            }).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            Assert.That(page.Count, Is.LessThanOrEqualTo(2));
            seen.AddRange(page.Select(item => item.GetId()));
            afterTime = page[^1].GroundDelivery!.ExpiresAt;
            afterId = page[^1].GroundDelivery!.GetId();
            if (seen.Count == 2)
            {
                await using var consume = new EntityDataContext();
                var consumedId = page[0].GetId();
                await consume.Set<StoredItem>().Where(item => item.Id == consumedId)
                    .ExecuteUpdateAsync(update => update.SetProperty(item => item.GroundDeliveryId, (Guid?)null)).ConfigureAwait(false);
            }

            Assert.That(seen.Count, Is.LessThanOrEqualTo(fixtures.Count));
        }

        Assert.That(seen, Is.EquivalentTo(fixtures.Select(fixture => fixture.SourceId)));
        Assert.That(seen.Distinct().Count(), Is.EqualTo(5));
    }

    /// <summary>
    /// A marker attached to an inventory-owned item is never republished as a drop.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task RecoveryPageRejectsInventoryOwnershipAsync()
    {
        var fixture = await this.CreateDeliveryAsync().ConfigureAwait(false);
        await using (var prepare = new EntityDataContext())
        {
            await prepare.Set<StoredItem>().Where(item => item.Id == fixture.SourceId).ExecuteUpdateAsync(update => update
                .SetProperty(item => EF.Property<Guid?>(item, "ItemStorageId"), fixture.FirstStorageId)).ConfigureAwait(false);
        }

        using var reader = this._provider.CreateNewPlayerContext(this._configuration);
        Assert.That(await reader.GetGroundItemPageAsync(QueryFor(fixture)).ConfigureAwait(false), Is.Empty);
    }

    /// <summary>
    /// Missing cached definitions must fail closed instead of returning id-only placeholders.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task RecoveryPageRejectsUnknownConfigurationAsync()
    {
        var fixture = await this.CreateDeliveryAsync().ConfigureAwait(false);
        var isolatedProvider = new PersistenceContextProvider(NullLoggerFactory.Instance, null);
        using var reader = isolatedProvider.CreateNewPlayerContext(new Persistence.EntityFramework.Model.GameConfiguration { Id = Guid.NewGuid() });
        Assert.ThrowsAsync<InvalidDataException>(async () =>
            await reader.GetGroundItemPageAsync(QueryFor(fixture)).ConfigureAwait(false));
        Assert.That(GetDatabaseContext(reader).ChangeTracker.Entries(), Is.Empty);
        await using var verify = new EntityDataContext();
        Assert.That(await verify.Set<StoredItem>().AnyAsync(item => item.Id == fixture.SourceId && item.GroundDeliveryId == fixture.DeliveryId).ConfigureAwait(false), Is.True);
    }

    /// <summary>
    /// Downgrade cannot discard any outstanding generation or its complete item aggregate.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task PendingDeliveryRejectsDowngradeWithoutLosingOwnershipAsync()
    {
        var fixture = await this.CreateDeliveryAsync(includeSet: true).ConfigureAwait(false);
        await using (var schema = new EntityDataContext())
        {
            Assert.That(async () => await schema.GetService<IMigrator>()
                    .MigrateAsync("20261115162237_AddCharacterRecoveryStorage").ConfigureAwait(false),
                Throws.Exception.With.Message.Contains("Consume durable ground deliveries before downgrading"));
            Assert.That(await schema.Database.GetAppliedMigrationsAsync().ConfigureAwait(false),
                Does.Contain("20261117185710_AddGroundItemDelivery"));
        }

        // Do not upgrade again: a new reader must work on the unchanged schema.
        using var reader = this._provider.CreateNewPlayerContext(this._configuration);
        var source = (await reader.GetGroundItemPageAsync(QueryFor(fixture)).ConfigureAwait(false)).Single();
        var delivery = source.GroundDelivery ?? throw new InvalidOperationException("The original delivery was lost during a rejected downgrade.");
        Assert.That(source.GetId(), Is.EqualTo(fixture.SourceId));
        Assert.That(delivery.GetId(), Is.EqualTo(fixture.DeliveryId));
        Assert.That(delivery.OwnerCharacterId, Is.EqualTo(fixture.OwnerId));
        Assert.That(delivery.ExpiresAt, Is.EqualTo(fixture.ExpiresAt));
        Assert.That(source.ItemOptions.Single().GetId(), Is.EqualTo(fixture.OptionId));
        Assert.That(source.ItemSetGroups.Single().GetId(), Is.EqualTo(fixture.SetId));
        Assert.That(source.Level, Is.EqualTo(7));
        Assert.That(source.HasSkill, Is.True);
        Assert.That(source.Durability, Is.EqualTo(2));
    }

    private static GroundItemQuery QueryFor(DeliveryFixture fixture) => new()
    {
        HostServerId = fixture.HostId,
        MapDefinitionId = fixture.MapId,
        MapInstanceId = fixture.InstanceId,
    };

    private static DbContext GetDatabaseContext(IContext context) => ((EntityFrameworkContextBase)context).Context;

    private static async Task<StoredItem> LoadSourceAsync(EntityFrameworkContextBase context, Guid id)
        => await context.Context.Set<StoredItem>().Include(item => item.RawGroundDelivery)
            .Include(item => item.RawItemOptions).SingleAsync(item => item.Id == id).ConfigureAwait(false);

    private static async Task StageAsync(EntityFrameworkContextBase context, StoredItem source, Guid storageId, Guid targetId, string operation)
    {
        if (operation == "pickup")
        {
            var delivery = source.GroundDelivery!;
            source.GroundDelivery = null;
            context.Context.Entry(source).Property<Guid?>("ItemStorageId").CurrentValue = storageId;
            Assert.That(await context.DeleteAsync(delivery).ConfigureAwait(false), Is.True);
        }
        else
        {
            Assert.That(await context.DeleteAsync(source).ConfigureAwait(false), Is.True);
            if (operation == "stack")
            {
                var target = await context.Context.Set<StoredItem>().SingleAsync(item => item.Id == targetId).ConfigureAwait(false);
                target.Durability += source.Durability;
            }
        }
    }

    private static async Task<bool> TrySaveAsync(IContext context)
    {
        try
        {
            return await context.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (PersistenceConflictException rejection)
        {
            Assert.That(rejection.InnerException, Is.TypeOf<DbUpdateConcurrencyException>(),
                "An unrelated constraint violation or deadlock cannot count as an expected generation conflict.");
            return false;
        }
    }

    private EntityFrameworkContextBase CreateContender(bool trade)
        => trade
            ? (EntityFrameworkContextBase)this._provider.CreateNewTradeContext()
            : (EntityFrameworkContextBase)this._provider.CreateNewPlayerContext(this._configuration);

    private async Task<DeliveryFixture> CreateDeliveryAsync(bool includeSet = false)
    {
        using var context = this._provider.CreateNewPlayerContext(this._configuration);
        var set = includeSet ? this._configuration.ItemSetGroups.SelectMany(group => group.Items)
            .First(value => value.ItemDefinition!.PossibleItemOptions.Any(options => options.PossibleOptions.Any())) : null;
        var definition = set?.ItemDefinition ?? this._configuration.Items.First(item => item.PossibleItemOptions.Any(options => options.PossibleOptions.Any()));
        var source = context.CreateNew<Item>();
        source.Definition = definition;
        source.Durability = 2;
        source.Level = 7;
        source.HasSkill = true;
        if (set is not null)
        {
            source.ItemSetGroups.Add(set);
        }
        var option = context.CreateNew<ItemOptionLink>();
        option.ItemOption = definition.PossibleItemOptions.SelectMany(options => options.PossibleOptions).First();
        option.Level = 1;
        option.Index = 2;
        source.ItemOptions.Add(option);
        var delivery = context.CreateNew<GroundItemDelivery>();
        delivery.HostServerId = Guid.NewGuid();
        delivery.MapDefinitionId = this._configuration.Maps.First().GetId();
        delivery.MapInstanceId = Guid.NewGuid();
        delivery.OwnerCharacterId = Guid.NewGuid();
        delivery.PositionX = 123;
        delivery.PositionY = 45;
        delivery.DroppedAt = new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc);
        delivery.OwnerPriorityUntil = delivery.DroppedAt.AddSeconds(10);
        delivery.ExpiresAt = delivery.DroppedAt.AddMinutes(5);
        source.GroundDelivery = delivery;
        var storages = new ItemStorage[2];
        var targets = new Item[2];
        for (var index = 0; index < storages.Length; index++)
        {
            storages[index] = context.CreateNew<ItemStorage>();
            storages[index].Money = 4321;
            targets[index] = context.CreateNew<Item>();
            targets[index].Definition = definition;
            targets[index].Durability = 3;
            targets[index].ItemSlot = 12;
            storages[index].Items.Add(targets[index]);
        }

        Assert.That(await context.SaveChangesAsync().ConfigureAwait(false), Is.True);
        return new DeliveryFixture(source.GetId(), delivery.GetId(), option.GetId(), option.ItemOption.GetId(), delivery.ExpiresAt,
            delivery.HostServerId, delivery.MapDefinitionId, delivery.MapInstanceId, delivery.OwnerCharacterId,
            storages[0].GetId(), targets[0].GetId(), storages[1].GetId(), targets[1].GetId(), set?.GetId());
    }

    private sealed record DeliveryFixture(Guid SourceId, Guid DeliveryId, Guid OptionId, Guid OptionDefinitionId, DateTime ExpiresAt,
        Guid HostId, Guid MapId, Guid? InstanceId, Guid OwnerId,
        Guid FirstStorageId, Guid FirstTargetId, Guid SecondStorageId, Guid SecondTargetId, Guid? SetId);
}
