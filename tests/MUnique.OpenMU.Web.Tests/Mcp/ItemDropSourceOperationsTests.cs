// <copyright file="ItemDropSourceOperationsTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.Mcp;

using System.Text.Json.Nodes;
using System.Threading;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Configuration.Quests;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.AdminAuth;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.Persistence.Initialization;
using MUnique.OpenMU.Web.AdminPanel.Operations;

/// <summary>Exercises translated item resolution and candidate sources against actual drop eligibility boundaries.</summary>
[TestFixture]
public sealed class ItemDropSourceOperationsTests
{
    private InMemoryPersistenceContextProvider _persistence = null!;
    private IContext _context = null!;
    private GameConfiguration _config = null!;
    private GameMapDefinition _map = null!;
    private ItemDefinition _item = null!;
    private Mock<IDataSource<GameConfiguration>> _source = null!;

    /// <summary>Builds an isolated English-only legacy configuration and loads registered source translations.</summary>
    [SetUp]
    public void SetUp()
    {
        ConfigurationNameSources.Register();
        this._persistence = new InMemoryPersistenceContextProvider();
        this._context = this._persistence.CreateNewContext();
        this._config = this._context.CreateNew<GameConfiguration>();
        this._map = this._context.CreateNew<GameMapDefinition>();
        this._map.Number = 8;
        this._map.Name = "Tarkan";
        this._config.Maps.Add(this._map);
        this._item = this._context.CreateNew<ItemDefinition>();
        this._item.Group = 12;
        this._item.Number = 19;
        this._item.Name = "Orb of Death Stab";
        this._item.DropLevel = 72;
        this._item.DropsFromMonsters = true;
        this._config.Items.Add(this._item);
        this._source = new Mock<IDataSource<GameConfiguration>>();
        this._source.Setup(source => source.GetOwnerAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(this._config);
    }

    /// <summary>Disposes the isolated persistence context.</summary>
    [TearDown]
    public void TearDown() => this._context.Dispose();

    /// <summary>Chinese partial, client-style skill-orb suffix, English and invariant keys resolve the same item.</summary>
    /// <param name="text">The player's item name.</param>
    [TestCase("袭风")]
    [TestCase("袭风刺技能石")]
    [TestCase("Orb of Death Stab")]
    [TestCase("12/19")]
    public async Task FindsLegacyEnglishItemThroughAliasesAsync(string text)
    {
        this.AddRandomGroup();
        this.AddMonster(61, 82, "Beam Knight");
        var result = await this.CreateOperations().FindAsync(text, "死亡沙漠", null, 0, 10, default).ConfigureAwait(false);
        Assert.That(result["status"]!.GetValue<string>(), Is.EqualTo("candidates"));
        Assert.That(result["item"]!["key"]!.GetValue<string>(), Is.EqualTo("12/19"));
        Assert.That(result["item"]!["name"]!.GetValue<string>(), Is.EqualTo("袭风刺之石"));
        Assert.That(result["items"]!.AsArray().Single()!["monster"]!.GetValue<string>(), Is.EqualTo("黑炎魔"));
        Assert.That(result["items"]!.AsArray().Single()!["selection"]!.GetValue<string>(), Is.EqualTo("RandomItem"));
        Assert.That(this._item.Name.Value, Is.EqualTo("Orb of Death Stab"), "Lookup must not rename persisted content.");
    }

    /// <summary>A numeric map filter never matches another map merely containing that digit in its name.</summary>
    [Test]
    public async Task NumericMapFilterIsExactAsync()
    {
        this.AddRandomGroup();
        this.AddMonster(61, 82, "Beam Knight");
        var other = this._context.CreateNew<GameMapDefinition>();
        other.Number = 9;
        other.Name = "Event 8";
        foreach (var spawn in this._map.MonsterSpawns)
        {
            other.MonsterSpawns.Add(spawn);
        }

        foreach (var group in this._map.DropItemGroups)
        {
            other.DropItemGroups.Add(group);
        }

        this._config.Maps.Add(other);
        var result = await this.CreateOperations().FindAsync("袭风", "8", null, 0, 10, default).ConfigureAwait(false);
        Assert.That(result["items"]!.AsArray(), Has.Count.EqualTo(1));
        Assert.That(result["items"]!.AsArray().Single()!["mapNumber"]!.GetValue<int>(), Is.EqualTo(8));
    }

    /// <summary>The inclusive minimum, exclusive twelve-level gap and configured maximum match the live generator.</summary>
    [Test]
    public async Task RandomPoolHonorsLevelBoundsAndActiveContentAsync()
    {
        this.AddRandomGroup();
        this.AddMonster(1, 71);
        this.AddMonster(2, 72);
        this.AddMonster(3, 83);
        this.AddMonster(4, 84);
        this.AddMonster(5, 80).IsActive = false;
        var result = await this.CreateOperations().FindAsync("袭风", "", null, 0, 10, default).ConfigureAwait(false);
        Assert.That(Numbers(result), Is.EquivalentTo(new[] { 2, 3 }));
        this._item.MaximumDropLevel = 80;
        result = await this.CreateOperations().FindAsync("袭风", "", null, 0, 10, default).ConfigureAwait(false);
        Assert.That(Numbers(result), Is.EqualTo(new[] { 2 }));
        this._item.IsActive = false;
        result = await this.CreateOperations().FindAsync("袭风", "", null, 0, 10, default).ConfigureAwait(false);
        Assert.That(result["status"]!.GetValue<string>(), Is.EqualTo("item_inactive"));
    }

    /// <summary>Detached global catalog groups are not reported as active drops.</summary>
    [Test]
    public async Task CatalogAloneIsNotADropSourceAsync()
    {
        var group = this._context.CreateNew<DropItemGroup>();
        group.Chance = 0.5;
        group.PossibleItems.Add(this._item);
        this._config.DropItemGroups.Add(group);
        this.AddMonster(1, 72);
        var result = await this.CreateOperations().FindAsync("袭风", "", null, 0, 10, default).ConfigureAwait(false);
        Assert.That(result["total"]!.GetValue<int>(), Is.Zero);
        Assert.That(result["status"]!.GetValue<string>(), Is.EqualTo("no_configured_candidates"));
    }

    /// <summary>Specific monster groups bypass normal level filtering, but map lists and socket pools do not.</summary>
    [Test]
    public async Task SpecificAndSocketRoutesRemainDistinctAsync()
    {
        var monster = this.AddMonster(1, 150);
        var group = this._context.CreateNew<DropItemGroup>();
        group.Chance = 1;
        group.PossibleItems.Add(this._item);
        monster.DropItemGroups.Add(group);
        this._item.DropsFromMonsters = false;
        var result = await this.CreateOperations().FindAsync("袭风", "", null, 0, 10, default).ConfigureAwait(false);
        Assert.That(Numbers(result), Is.EqualTo(new[] { 1 }));
        monster.DropItemGroups.Clear();
        this._map.DropItemGroups.Add(group);
        result = await this.CreateOperations().FindAsync("袭风", "", null, 0, 10, default).ConfigureAwait(false);
        Assert.That(result["total"]!.GetValue<int>(), Is.Zero);
        this._map.DropItemGroups.Clear();
        this.AddRandomGroup();
        this._item.DropsFromMonsters = true;
        this._item.MaximumSockets = 5;
        this.AddMonster(2, 72);
        result = await this.CreateOperations().FindAsync("袭风", "", null, 0, 10, default).ConfigureAwait(false);
        Assert.That(result["total"]!.GetValue<int>(), Is.Zero);
    }

    /// <summary>Quest-only groups remain conditional even if also stored in the global catalog.</summary>
    [Test]
    public async Task QuestNamesSelectTheCorrectLevelAndPreserveConditionsAsync()
    {
        this._item.Name = "Emperors Book;Ring of Honor||zh-CN=帝王之书;荣誉戒指";
        this._item.DropLevel = 0;
        this._item.MaximumItemLevel = 1;
        this._item.DropsFromMonsters = false;
        var monster = this.AddMonster(1, 80);
        var group = this._context.CreateNew<DropItemGroup>();
        group.Chance = 0.03;
        group.ItemLevel = 1;
        group.PossibleItems.Add(this._item);
        this._config.DropItemGroups.Add(group);
        var quest = this._context.CreateNew<QuestDefinition>();
        quest.Name = "Gain Hero Status";
        var requirement = this._context.CreateNew<QuestItemRequirement>();
        requirement.Item = this._item;
        requirement.DropItemGroup = group;
        quest.RequiredItems.Add(requirement);
        monster.Quests.Add(quest);
        var result = await this.CreateOperations().FindAsync("荣誉戒指", "", null, 0, 10, default).ConfigureAwait(false);
        var row = result["items"]!.AsArray().Single()!;
        Assert.That(row["requiresActiveQuest"]!.GetValue<bool>(), Is.True);
        Assert.That(row["itemLevel"]!.GetValue<int>(), Is.EqualTo(1));
        Assert.That(row["itemName"]!.GetValue<string>(), Is.EqualTo("荣誉戒指"));
        result = await this.CreateOperations().FindAsync("帝王之书", "", null, 0, 10, default).ConfigureAwait(false);
        Assert.That(result["total"]!.GetValue<int>(), Is.Zero);
    }

    /// <summary>Ambiguity, pagination and empty results are explicit, with no unsupported no-drop conclusion.</summary>
    [Test]
    public async Task AmbiguityAndPaginationAreExplicitAsync()
    {
        this.AddRandomGroup();
        this.AddMonster(1, 72);
        this.AddMonster(2, 73);
        var result = await this.CreateOperations().FindAsync("袭风", "", null, 1, 1, default).ConfigureAwait(false);
        Assert.That(result["total"]!.GetValue<int>(), Is.EqualTo(2));
        Assert.That(Numbers(result), Is.EqualTo(new[] { 2 }));
        var other = this._context.CreateNew<ItemDefinition>();
        other.Name = "Other||zh-CN=袭风护符";
        this._config.Items.Add(other);
        result = await this.CreateOperations().FindAsync("袭风", "", null, 0, 10, default).ConfigureAwait(false);
        Assert.That(result["status"]!.GetValue<string>(), Is.EqualTo("ambiguous_item"));
        Assert.That(result["items"]!.AsArray(), Is.Empty);
        result = await this.CreateOperations().FindAsync("不存在", "", null, 0, 10, default).ConfigureAwait(false);
        Assert.That(result["status"]!.GetValue<string>(), Is.EqualTo("item_not_found"));
    }

    /// <summary>Content discovery searches both persisted and source aliases, independent of the caller's language.</summary>
    [Test]
    public async Task ContentListSearchesChineseAndEnglishAsync()
    {
        var executor = AdminOperationTestServices.CreateExecutor();
        var operations = new ContentActivationOperations(this._persistence, this._source.Object, executor, new AdminChangeConfirmation(TimeProvider.System), new AdminWriteSettings());
        foreach (var text in new[] { "袭风", "Death Stab" })
        {
            var result = await operations.ListAsync(ContentActivationType.Items, null, text, 0, 10, default).ConfigureAwait(false);
            Assert.That(result["total"]!.GetValue<int>(), Is.EqualTo(1));
            Assert.That(result["items"]!.AsArray().Single()!["aliases"]!.AsArray().Select(alias => alias!.GetValue<string>()), Does.Contain("袭风刺之石"));
        }
    }

    /// <summary>Invalid filters and unauthorized callers cannot enumerate gameplay data.</summary>
    [Test]
    public void RejectsInvalidFiltersAndUnauthorizedCallers()
    {
        Assert.ThrowsAsync<AdminOperationException>(async () => await this.CreateOperations().FindAsync("", "", null, 0, 10, default).ConfigureAwait(false));
        Assert.ThrowsAsync<AdminOperationException>(async () => await this.CreateOperations().FindAsync("袭风", "bad\nmap", null, 0, 10, default).ConfigureAwait(false));
        var executor = AdminOperationTestServices.CreateExecutor(actor: () => new AdminActor("id", "guest", "mcp", []));
        var operations = new ItemDropSourceOperations(this._persistence, this._source.Object, executor);
        Assert.ThrowsAsync<AdminOperationException>(async () => await operations.FindAsync("袭风", "", null, 0, 10, default).ConfigureAwait(false));
    }

    private static int[] Numbers(JsonObject result)
        => result["items"]!.AsArray().Select(row => row!["monsterNumber"]!.GetValue<int>()).ToArray();

    private ItemDropSourceOperations CreateOperations()
        => new(this._persistence, this._source.Object, AdminOperationTestServices.CreateExecutor());

    private void AddRandomGroup()
    {
        var group = this._context.CreateNew<DropItemGroup>();
        group.ItemType = SpecialItemType.RandomItem;
        group.Chance = 0.3;
        this._map.DropItemGroups.Add(group);
    }

    private MonsterDefinition AddMonster(short number, byte level, string name = "Monster")
    {
        var monster = this._context.CreateNew<MonsterDefinition>();
        monster.Number = number;
        monster.Designation = name;
        monster.NumberOfMaximumItemDrops = 1;
        var attribute = this._context.CreateNew<MonsterAttribute>();
        attribute.AttributeDefinition = Stats.Level;
        attribute.Value = level;
        monster.Attributes.Add(attribute);
        this._config.Monsters.Add(monster);
        var spawn = this._context.CreateNew<MonsterSpawnArea>();
        spawn.MonsterDefinition = monster;
        spawn.Quantity = 1;
        this._map.MonsterSpawns.Add(spawn);
        return monster;
    }
}
