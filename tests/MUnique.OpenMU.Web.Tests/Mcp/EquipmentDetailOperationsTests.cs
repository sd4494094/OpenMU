// <copyright file="EquipmentDetailOperationsTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.Mcp;

using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.AdminAuth;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.Persistence.Initialization;
using MUnique.OpenMU.Web.AdminPanel.Operations;

/// <summary>Checks equipment queries against real Season Six data and configurable formulas.</summary>
[TestFixture]
public sealed class EquipmentDetailOperationsTests
{
    private InMemoryPersistenceContextProvider _persistence = null!;
    private IContext _context = null!;
    private GameConfiguration _configuration = null!;
    private Mock<IDataSource<GameConfiguration>> _source = null!;

    /// <summary>Initializes an isolated real configuration, including level tables and item stat profiles.</summary>
    [SetUp]
    public async Task SetUpAsync()
    {
        ConfigurationNameSources.Register();
        this._persistence = new InMemoryPersistenceContextProvider();
        await new MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.DataInitialization(this._persistence, NullLoggerFactory.Instance)
            .CreateInitialDataAsync(1, false).ConfigureAwait(false);
        this._context = this._persistence.CreateNewContext();
        this._configuration = (await this._context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        this._source = new Mock<IDataSource<GameConfiguration>>();
        this._source.Setup(source => source.GetOwnerAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(this._configuration);
    }

    /// <summary>Disposes the isolated context.</summary>
    [TearDown]
    public void TearDown() => this._context.Dispose();

    /// <summary>Aliases and item keys return final requirements rather than the raw requirement coefficients.</summary>
    /// <param name="text">The player query.</param>
    /// <param name="level">The enhancement level.</param>
    /// <param name="minimum">Expected minimum physical attack.</param>
    /// <param name="maximum">Expected maximum physical attack.</param>
    /// <param name="strength">Expected strength requirement.</param>
    /// <param name="agility">Expected agility requirement.</param>
    /// <param name="requiredLevel">Expected independent level requirement.</param>
    [TestCase("断月之光", 0, 107, 115, 507, 179, 0)]
    [TestCase("Knight Blade", 15, 173, 181, 663, 230, 0)]
    [TestCase("0/20", 0, 107, 115, 507, 179, 0)]
    [TestCase("龙骨巨晶剑", 0, 122, 135, 461, 174, 380)]
    [TestCase("Bone Blade", 15, 188, 201, 596, 221, 380)]
    public async Task CalculatesRealEquipmentAsync(string text, byte level, int minimum, int maximum, int strength, int agility, int requiredLevel)
    {
        var before = this._context.HasChanges;
        var result = await this.Operations().GetAsync(text, level, default).ConfigureAwait(false);
        Assert.That(result["status"]!.GetValue<string>(), Is.EqualTo("calculated"));
        var details = result["details"]!;
        Assert.Multiple(() =>
        {
            Assert.That(details["physicalAttack"]!["minimum"]!.GetValue<float>(), Is.EqualTo(minimum));
            Assert.That(details["physicalAttack"]!["maximum"]!.GetValue<float>(), Is.EqualTo(maximum));
            Assert.That(Requirement(details, Stats.TotalStrength.Designation), Is.EqualTo(strength));
            Assert.That(Requirement(details, Stats.TotalAgility.Designation), Is.EqualTo(agility));
            Assert.That(details["levelRequirement"]!.GetValue<int>(), Is.EqualTo(requiredLevel));
            Assert.That(details["qualifiedClasses"]!.AsArray().Select(row => row!["name"]!.GetValue<string>()), Does.Contain("神骑士"));
            Assert.That(this._context.HasChanges, Is.EqualTo(before), "A read must not create a persisted item or edit configuration.");
        });
    }

    /// <summary>Changes to live configuration tables and coefficients affect results immediately.</summary>
    [Test]
    public async Task UsesConfiguredTablesAndRequirementsAsync()
    {
        var definition = this._configuration.Items.Single(item => item.Group == 0 && item.Number == 20);
        var damage = definition.BasePowerUpAttributes.Single(powerUp => powerUp.TargetAttribute == Stats.MinimumPhysBaseDmgByWeapon);
        damage.BonusPerLevelTable!.BonusPerLevel.Single(bonus => bonus.Level == 15).AdditionalValue = 91;
        definition.Requirements.Single(requirement => requirement.Attribute == Stats.TotalStrengthRequirementValue).MinimumValue = 100;
        var result = await this.Operations().GetAsync("0/20", 15, default).ConfigureAwait(false);
        Assert.That(result["details"]!["physicalAttack"]!["minimum"]!.GetValue<float>(), Is.EqualTo(198));
        Assert.That(Requirement(result["details"]!, Stats.TotalStrength.Designation), Is.EqualTo(575));
    }

    /// <summary>Ambiguity, missing data, inactive items and non-equipment do not become invented values.</summary>
    [Test]
    public async Task ReportsLookupAndConfigurationBoundariesAsync()
    {
        var operations = this.Operations();
        var ambiguous = await operations.GetAsync("剑", 0, default).ConfigureAwait(false);
        Assert.That(ambiguous["status"]!.GetValue<string>(), Is.EqualTo("ambiguous_item"));
        Assert.That(ambiguous["details"], Is.Null);
        Assert.That((await operations.GetAsync("不存在的装备", 0, default).ConfigureAwait(false))["status"]!.GetValue<string>(), Is.EqualTo("item_not_found"));
        Assert.That((await operations.GetAsync("14/0", 0, default).ConfigureAwait(false))["status"]!.GetValue<string>(), Is.EqualTo("not_equipment"));
        var item = this._configuration.Items.Single(definition => definition.Group == 0 && definition.Number == 20);
        item.IsActive = false;
        Assert.That((await operations.GetAsync("0/20", 0, default).ConfigureAwait(false))["status"]!.GetValue<string>(), Is.EqualTo("item_inactive"));
        item.IsActive = true;
        this._configuration.ItemStatProfiles.Clear();
        Assert.That((await operations.GetAsync("0/20", 0, default).ConfigureAwait(false))["status"]!.GetValue<string>(), Is.EqualTo("missing_stat_profile"));
    }

    /// <summary>Armor has no weapon attack range; class restrictions remain specific to the queried equipment.</summary>
    [Test]
    public async Task DistinguishesArmorAndClassRestrictionsAsync()
    {
        var result = await this.Operations().GetAsync("7/0", 0, default).ConfigureAwait(false);
        Assert.That(result["status"]!.GetValue<string>(), Is.EqualTo("calculated"));
        Assert.That(result["details"]!["physicalAttack"], Is.Null);
        result = await this.Operations().GetAsync("0/18", 0, default).ConfigureAwait(false);
        Assert.That(result["details"]!["qualifiedClasses"]!.AsArray().Select(row => row!["name"]!.GetValue<string>()), Does.Not.Contain("神骑士"));
    }

    /// <summary>Viewer authorization and validated enhancement bounds apply before computing data.</summary>
    [Test]
    public void RejectsInvalidInputAndUnauthorizedAccess()
    {
        Assert.ThrowsAsync<AdminOperationException>(async () => await this.Operations().GetAsync("", 0, default).ConfigureAwait(false));
        Assert.ThrowsAsync<AdminOperationException>(async () => await this.Operations().GetAsync("bad\nname", 0, default).ConfigureAwait(false));
        Assert.ThrowsAsync<AdminOperationException>(async () => await this.Operations().GetAsync("0/20", 16, default).ConfigureAwait(false));
        var executor = AdminOperationTestServices.CreateExecutor(actor: () => new AdminActor("id", "guest", "mcp", []));
        var operations = new EquipmentDetailOperations(this._persistence, this._source.Object, executor);
        Assert.ThrowsAsync<AdminOperationException>(async () => await operations.GetAsync("0/20", 0, default).ConfigureAwait(false));
    }


    /// <summary>Real class filters include compatible swords and exclude magic gladiator weapons, gloves and two-handed swords.</summary>
    /// <param name="characterClass">The class alias or number.</param>
    [TestCase("神骑士")]
    [TestCase("Blade Master")]
    [TestCase("7")]
    public async Task SearchesClassAndOneHandedSwordsAsync(string characterClass)
    {
        var before = this._context.HasChanges;
        var result = await this.Operations().SearchAsync(characterClass, "Sword", "OneHanded", "", 0, 0, 20, default).ConfigureAwait(false);
        Assert.That(result["status"]!.GetValue<string>(), Is.EqualTo("candidates"));
        var rows = result["items"]!.AsArray();
        var keys = rows.Select(row => row!["key"]!.GetValue<string>()).ToArray();
        Assert.That(keys, Does.Contain("0/20").And.Contain("0/22").And.Contain("0/26").And.Contain("0/27"));
        Assert.That(keys, Does.Not.Contain("0/18").And.Not.Contain("0/24").And.Not.Contain("0/28").And.Not.Contain("0/33"));
        Assert.That(rows.All(row => row!["weaponType"]!.GetValue<string>() == "Sword" && row["handedness"]!.GetValue<string>() == "OneHanded"), Is.True);
        Assert.That(rows.All(row => row!["qualifiedClasses"]!.AsArray().Any(c => c!["name"]!.GetValue<string>() == "神骑士")), Is.True);
        var bone = rows.Single(row => row!["key"]!.GetValue<string>() == "0/22")!;
        Assert.That(bone["physicalAttack"]!["minimum"]!.GetValue<float>(), Is.EqualTo(122));
        Assert.That(bone["attackSpeed"]!.GetValue<float>(), Is.EqualTo(40));
        Assert.That(bone["maximumSockets"]!.GetValue<int>(), Is.Zero);
        var flamberge = rows.Single(row => row!["key"]!.GetValue<string>() == "0/26")!;
        Assert.That(flamberge["name"]!.GetValue<string>(), Is.EqualTo("虚伪之剑"));
        Assert.That(flamberge["maximumSockets"]!.GetValue<int>(), Is.EqualTo(3));
        Assert.That(flamberge["physicalAttack"]!["minimum"]!.GetValue<float>(), Is.EqualTo(115));
        Assert.That(this._context.HasChanges, Is.EqualTo(before));
    }

    /// <summary>Configured weapon markers distinguish gloves, ranged weapons, staves and two-handed weapons.</summary>
    [Test]
    public async Task SeparatesWeaponKindsAndHandsAsync()
    {
        var two = await this.Operations().SearchAsync("神骑士", "Sword", "TwoHanded", "", 0, 0, 20, default).ConfigureAwait(false);
        var keys = two["items"]!.AsArray().Select(row => row!["key"]!.GetValue<string>());
        Assert.That(keys, Does.Contain("0/24").And.Not.Contain("0/22"));
        var glove = await this.Operations().SearchAsync("", "Glove", "OneHanded", "", 0, 0, 20, default).ConfigureAwait(false);
        Assert.That(glove["items"]!.AsArray().Select(row => row!["key"]!.GetValue<string>()), Does.Contain("0/33").And.Not.Contain("0/20"));
        foreach (var kind in new[] { "Bow", "Crossbow" })
        {
            var ranged = await this.Operations().SearchAsync("", kind, "Ranged", "", 0, 0, 20, default).ConfigureAwait(false);
            Assert.That(ranged["total"]!.GetValue<int>(), Is.GreaterThan(0));
            Assert.That(ranged["items"]!.AsArray().All(row => row!["weaponType"]!.GetValue<string>() == kind), Is.True);
        }

        var staff = await this.Operations().SearchAsync("", "Staff", "TwoHanded", "", 0, 0, 20, default).ConfigureAwait(false);
        Assert.That(staff["total"]!.GetValue<int>(), Is.GreaterThan(0));
        Assert.That(staff["items"]!.AsArray().All(row => row!["staffRise"] is not null), Is.True);
    }

    /// <summary>Filtering, pagination and enhancements share the same configured calculation as the detail query.</summary>
    [Test]
    public async Task SearchHonorsNamesLevelsPaginationAndActiveStateAsync()
    {
        var all = await this.Operations().SearchAsync("神骑士", "Sword", "OneHanded", "", 0, 0, 20, default).ConfigureAwait(false);
        var first = await this.Operations().SearchAsync("神骑士", "Sword", "OneHanded", "", 0, 0, 2, default).ConfigureAwait(false);
        var next = await this.Operations().SearchAsync("神骑士", "Sword", "OneHanded", "", 0, 2, 2, default).ConfigureAwait(false);
        Assert.That(first["hasMore"]!.GetValue<bool>(), Is.True);
        Assert.That(first["total"]!.GetValue<int>(), Is.EqualTo(all["total"]!.GetValue<int>()));
        Assert.That(first["items"]!.AsArray().Concat(next["items"]!.AsArray()).Select(row => row!["key"]!.GetValue<string>()),
            Is.EqualTo(all["items"]!.AsArray().Take(4).Select(row => row!["key"]!.GetValue<string>())));
        foreach (var text in new[] { "断月之光", "Knight Blade", "0/20" })
        {
            var found = await this.Operations().SearchAsync("神骑士", "Sword", "OneHanded", text, 15, 0, 8, default).ConfigureAwait(false);
            var row = found["items"]!.AsArray().Single()!;
            Assert.That(row["physicalAttack"]!["minimum"]!.GetValue<float>(), Is.EqualTo(173));
            var detail = await this.Operations().GetAsync(text, 15, default).ConfigureAwait(false);
            Assert.That(JsonNode.DeepEquals(row["physicalAttack"], detail["details"]!["physicalAttack"]), Is.True);
        }

        var definition = this._configuration.Items.Single(item => item.Group == 0 && item.Number == 20);
        definition.IsActive = false;
        var inactive = await this.Operations().SearchAsync("神骑士", "Sword", "OneHanded", "0/20", 0, 0, 8, default).ConfigureAwait(false);
        Assert.That(inactive["status"]!.GetValue<string>(), Is.EqualTo("no_matching_equipment"));
        definition.IsActive = true;
        definition.MaximumItemLevel = 9;
        Assert.That((await this.Operations().SearchAsync("神骑士", "Sword", "OneHanded", "0/20", 15, 0, 8, default).ConfigureAwait(false))["total"]!.GetValue<int>(), Is.Zero);
    }

    /// <summary>Unrecognized or ambiguous classes and fabricated names return explicit non-success states.</summary>
    [Test]
    public async Task SearchDoesNotInventClassOrEquipmentMatchesAsync()
    {
        Assert.That((await this.Operations().SearchAsync("不存在的职业", "Sword", "All", "", 0, 0, 8, default).ConfigureAwait(false))["status"]!.GetValue<string>(), Is.EqualTo("class_not_found"));
        Assert.That((await this.Operations().SearchAsync("骑士", "Sword", "All", "", 0, 0, 8, default).ConfigureAwait(false))["status"]!.GetValue<string>(), Is.EqualTo("ambiguous_class"));
        Assert.That((await this.Operations().SearchAsync("神骑士", "Sword", "OneHanded", "神圣风暴之剑", 0, 0, 8, default).ConfigureAwait(false))["status"]!.GetValue<string>(), Is.EqualTo("no_matching_equipment"));
        this._configuration.ItemStatProfiles.Clear();
        Assert.That((await this.Operations().SearchAsync("神骑士", "Sword", "All", "", 0, 0, 8, default).ConfigureAwait(false))["status"]!.GetValue<string>(), Is.EqualTo("missing_stat_profile"));
    }

    /// <summary>Search validates public boundaries before reading data and keeps Viewer authorization.</summary>
    [Test]
    public void SearchRejectsInvalidFiltersAndUnauthorizedAccess()
    {
        Assert.ThrowsAsync<AdminOperationException>(async () => await this.Operations().SearchAsync("", "Gun", "All", "", 0, 0, 8, default).ConfigureAwait(false));
        Assert.ThrowsAsync<AdminOperationException>(async () => await this.Operations().SearchAsync("", "Sword", "single", "", 0, 0, 8, default).ConfigureAwait(false));
        Assert.ThrowsAsync<AdminOperationException>(async () => await this.Operations().SearchAsync("", "Sword", "All", "", 16, 0, 8, default).ConfigureAwait(false));
        Assert.ThrowsAsync<AdminOperationException>(async () => await this.Operations().SearchAsync("", "Sword", "All", "", 0, 0, 21, default).ConfigureAwait(false));
        Assert.ThrowsAsync<AdminOperationException>(async () => await this.Operations().SearchAsync("bad\nclass", "Sword", "All", "", 0, 0, 8, default).ConfigureAwait(false));
        var executor = AdminOperationTestServices.CreateExecutor(actor: () => new AdminActor("id", "guest", "mcp", []));
        var operations = new EquipmentDetailOperations(this._persistence, this._source.Object, executor);
        Assert.ThrowsAsync<AdminOperationException>(async () => await operations.SearchAsync("", "All", "All", "", 0, 0, 8, default).ConfigureAwait(false));
    }

    private static int Requirement(JsonNode details, string? attribute)
        => details["attributeRequirements"]!.AsArray().Single(row => row!["attribute"]!.GetValue<string>() == attribute)!["value"]!.GetValue<int>();

    private EquipmentDetailOperations Operations()
        => new(this._persistence, this._source.Object, AdminOperationTestServices.CreateExecutor());
}
