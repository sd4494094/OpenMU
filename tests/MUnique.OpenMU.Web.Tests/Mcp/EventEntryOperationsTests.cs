// <copyright file="EventEntryOperationsTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.Mcp;

using System.Threading;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.Persistence.Initialization;
using MUnique.OpenMU.Web.AdminPanel.Operations;

/// <summary>Checks admission boundaries, class advancement and edits to real configuration values.</summary>
[TestFixture]
public sealed class EventEntryOperationsTests
{
    private InMemoryPersistenceContextProvider _persistence = null!;
    private IContext _context = null!;
    private GameConfiguration _configuration = null!;
    private Mock<IDataSource<GameConfiguration>> _source = null!;

    /// <summary>Creates isolated event tiers and ordinary, special and master classes.</summary>
    [SetUp]
    public void SetUp()
    {
        ConfigurationNameSources.Register();
        this._persistence = new InMemoryPersistenceContextProvider();
        this._context = this._persistence.CreateNewContext();
        this._configuration = this._context.CreateNew<GameConfiguration>();
        this._source = new Mock<IDataSource<GameConfiguration>>();
        this._source.Setup(s => s.GetOwnerAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(this._configuration);
        foreach (var (number, special, master) in new[] { (6, false, false), (48, true, false), (7, false, true), (50, true, true) })
        {
            var characterClass = this._context.CreateNew<CharacterClass>();
            characterClass.Number = (byte)number;
            characterClass.Name = "Class " + number;
            characterClass.LevelWarpRequirementReductionPercent = special ? 33 : 0;
            characterClass.IsMasterClass = master;
            this._configuration.CharacterClasses.Add(characterClass);
        }

        this.AddTier(5, 231, 280, 211, 260);
        this.AddTier(6, 281, 330, 261, 310);
        this.AddTier(7, 331, 400, 311, 400);
        this.AddTier(8, 331, 400, 0, 400).RequiresMasterClass = true;
    }

    /// <summary>Disposes isolated persistence.</summary>
    [TearDown]
    public void TearDown() => this._context.Dispose();

    /// <summary>Inclusive boundaries and master qualification agree with the entry rule.</summary>
    /// <param name="level">Normal character level.</param>
    /// <param name="classNumber">Configured class including advancement.</param>
    /// <param name="expected">Matching tiers.</param>
    [TestCase(279, 6, new[] { 5 })]
    [TestCase(280, 6, new[] { 5 })]
    [TestCase(281, 6, new[] { 6 })]
    [TestCase(279, 48, new[] { 6 })]
    [TestCase(400, 6, new[] { 7 })]
    [TestCase(400, 7, new[] { 7, 8 })]
    [TestCase(400, 50, new[] { 7, 8 })]
    public async Task EvaluatesActualClassRulesAsync(int level, int classNumber, int[] expected)
    {
        var result = await this.Operations().ListAsync("血色城堡", level, classNumber, default).ConfigureAwait(false);
        var tiers = result["items"]!.AsArray().Where(r => r!["matchesLevelAndClass"]!.GetValue<bool>())
            .Select(r => r!["gameLevel"]!.GetValue<int>());
        Assert.That(tiers, Is.EquivalentTo(expected));
    }

    /// <summary>Chinese and English filters work without rewriting stored names or guessing unknown events.</summary>
    [Test]
    public async Task ReadsEditsAndAvailabilityWithoutHardCodedTablesAsync()
    {
        var tier = this._configuration.MiniGameDefinitions.First();
        tier.MaximumCharacterLevel = 279;
        tier.IsActive = false;
        var result = await this.Operations().ListAsync("BloodCastle", 280, 6, default).ConfigureAwait(false);
        var first = result["items"]!.AsArray().First()!;
        Assert.That(first["maximumCharacterLevel"]!.GetValue<int>(), Is.EqualTo(279));
        Assert.That(first["matchesLevelAndClass"]!.GetValue<bool>(), Is.False);
        Assert.That(first["available"]!.GetValue<bool>(), Is.False);
        Assert.That(first["ticketItemLevel"]!.GetValue<int>(), Is.EqualTo(5));
        result = await this.Operations().ListAsync("unknown-event", null, null, default).ConfigureAwait(false);
        Assert.That(result["status"]!.GetValue<string>(), Is.EqualTo("no_configured_event"));
        result = await this.Operations().ListAsync("Blood Castle", null, null, default).ConfigureAwait(false);
        Assert.That(result["total"]!.GetValue<int>(), Is.EqualTo(4));
        Assert.That(result["items"]!.AsArray().All(row => row!["matchesLevelAndClass"] is null), Is.True);
    }

    /// <summary>Unknown classes cannot silently become ordinary classes.</summary>
    [Test]
    public void RejectsUnknownClass()
        => Assert.ThrowsAsync<ArgumentException>(async () => await this.Operations().ListAsync("BloodCastle", 279, 255, default).ConfigureAwait(false));

    private EventEntryOperations Operations() => new(this._persistence, this._source.Object, AdminOperationTestServices.CreateExecutor());

    private MiniGameDefinition AddTier(byte number, int minimum, int maximum, int specialMinimum, int specialMaximum)
    {
        var tier = this._context.CreateNew<MiniGameDefinition>();
        tier.Type = MiniGameType.BloodCastle;
        tier.Name = "Blood Castle " + number;
        tier.GameLevel = number;
        tier.MinimumCharacterLevel = minimum;
        tier.MaximumCharacterLevel = maximum;
        tier.MinimumSpecialCharacterLevel = specialMinimum;
        tier.MaximumSpecialCharacterLevel = specialMaximum;
        tier.TicketItemLevel = number;
        var map = this._context.CreateNew<GameMapDefinition>();
        map.IsActive = true;
        tier.Entrance = this._context.CreateNew<ExitGate>();
        tier.Entrance.Map = map;
        this._configuration.MiniGameDefinitions.Add(tier);
        return tier;
    }
}
