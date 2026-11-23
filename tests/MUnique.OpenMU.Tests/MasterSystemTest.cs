// <copyright file="MasterSystemTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlayerActions.Character;
using MUnique.OpenMU.Persistence.InMemory;

/// <summary>
/// Tests the master level system.
/// </summary>
[TestFixture]
public class MasterSystemTest
{
    /// <summary>
    /// The master skill roots by their number. Like in the configuration, all skills of a root share the same instance.
    /// </summary>
    private readonly Dictionary<byte, MasterSkillRoot> _roots = [];

    private readonly int _skillIdRank1 = 1;
    private readonly int _skillIdRank2 = 2;
    private readonly int _skillIdRank3 = 3;

    private Player _player = null!;
    private Skill _skillRank1 = null!;
    private Skill _skillRank2 = null!;
    private Skill _skillRank3 = null!;
    private AddMasterPointAction _addAction = null!;

    /// <summary>
    /// Setups the test data.
    /// </summary>
    [SetUp]
    public async Task SetupAsync()
    {
        this._roots.Clear();
        this._player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var context = this._player.GameContext;
        this._skillRank1 = this.CreateSkill(1, 1, 1, null, this._player.SelectedCharacter!.CharacterClass!);
        this._skillRank2 = this.CreateSkill(2, 2, 1, null, this._player.SelectedCharacter!.CharacterClass!);
        this._skillRank3 = this.CreateSkill((short)this._skillIdRank3, 3, 1, null, this._player.SelectedCharacter!.CharacterClass!);
        this._skillRank3.MasterDefinition!.MinimumLevel = 10;
        context.Configuration.Skills.Add(this._skillRank1);
        context.Configuration.Skills.Add(this._skillRank2);
        context.Configuration.Skills.Add(this._skillRank3);
        this._addAction = new AddMasterPointAction();
    }

    /// <summary>
    /// Tests if the adding of master points failes because of insufficient level up points.
    /// </summary>
    [Test]
    public async Task FailedInsufficientLevelUpPointsAsync()
    {
        await this._addAction.AddMasterPointAsync(this._player, (ushort)this._skillIdRank1).ConfigureAwait(false);
        Assert.That(this._player.SelectedCharacter!.LearnedSkills, Is.Empty);
    }

    /// <summary>
    /// Tests if the adding of master points succeeds.
    /// </summary>
    [Test]
    public async Task SucceededAsync()
    {
        this._player.SelectedCharacter!.MasterLevelUpPoints = 1;
        await this._addAction.AddMasterPointAsync(this._player, (ushort)this._skillIdRank1).ConfigureAwait(false);
        Assert.That(this._player.SelectedCharacter.LearnedSkills, Is.Not.Empty);
    }

    /// <summary>
    /// Tests if the adding of master points fails because of an insufficient reached skill rank.
    /// </summary>
    [Test]
    public async Task RankNotSufficientAsync()
    {
        this._player.SelectedCharacter!.MasterLevelUpPoints = 1;
        await this._addAction.AddMasterPointAsync(this._player, (ushort)this._skillIdRank2).ConfigureAwait(false);
        Assert.That(this._player.SelectedCharacter.LearnedSkills, Is.Empty);
    }

    /// <summary>
    /// Tests if the adding of master points fails because the skill of the previous rank does not have the required level 10.
    /// </summary>
    [Test]
    public async Task PreviousRankTooLowLevelAsync()
    {
        this._player.SelectedCharacter!.MasterLevelUpPoints = 2;
        await this._addAction.AddMasterPointAsync(this._player, (ushort)this._skillIdRank1).ConfigureAwait(false);
        this._player.SelectedCharacter.LearnedSkills.First().Level = 9;
        await this._addAction.AddMasterPointAsync(this._player, (ushort)this._skillIdRank2).ConfigureAwait(false);
        Assert.That(this._player.SelectedCharacter.LearnedSkills, Has.Count.EqualTo(1));
        Assert.That(this._player.SelectedCharacter.LearnedSkills.First().Skill, Is.SameAs(this._skillRank1));
    }

    /// <summary>
    /// Tests if the adding of master points succeeds because the skill of the previous rank has the required level 10.
    /// </summary>
    [Test]
    public async Task PreviousRankEnoughLevelsAsync()
    {
        this._player.SelectedCharacter!.MasterLevelUpPoints = 2;
        await this._addAction.AddMasterPointAsync(this._player, (ushort)this._skillIdRank1).ConfigureAwait(false);
        this._player.SelectedCharacter.LearnedSkills.First().Level = 10;
        await this._addAction.AddMasterPointAsync(this._player, (ushort)this._skillIdRank2).ConfigureAwait(false);
        Assert.That(this._player.SelectedCharacter.LearnedSkills, Has.Count.EqualTo(2));
        Assert.That(this._player.SelectedCharacter.LearnedSkills.Any(l => l.Skill == this._skillRank1), Is.True);
        Assert.That(this._player.SelectedCharacter.LearnedSkills.Any(l => l.Skill == this._skillRank2), Is.True);
    }

    /// <summary>
    /// Tests if the adding of master points succeeds when a skill has a minium level of 10 and the character has enough master points.
    /// </summary>
    [Test]
    public async Task MinimumLevel10WithEnoughPointsAsync()
    {
        this._player.SelectedCharacter!.MasterLevelUpPoints = 2;
        await this._addAction.AddMasterPointAsync(this._player, (ushort)this._skillIdRank1).ConfigureAwait(false);
        this._player.SelectedCharacter.LearnedSkills.First().Level = 10;
        await this._addAction.AddMasterPointAsync(this._player, (ushort)this._skillIdRank2).ConfigureAwait(false);
        this._player.SelectedCharacter.LearnedSkills.Last().Level = 10;
        this._player.SelectedCharacter.MasterLevelUpPoints = 10;
        await this._addAction.AddMasterPointAsync(this._player, (ushort)this._skillIdRank3).ConfigureAwait(false);
        Assert.That(this._player.SelectedCharacter.LearnedSkills, Has.Count.EqualTo(3));
        Assert.That(this._player.SelectedCharacter.LearnedSkills.Any(l => l.Skill == this._skillRank3), Is.True);
    }

    /// <summary>
    /// Tests if the adding of master points succeeds when a skill has a minimum level of 10 and the character has not enough master points.
    /// </summary>
    [Test]
    public async Task MinimumLevel10WithoutEnoughPointsAsync()
    {
        this._player.SelectedCharacter!.MasterLevelUpPoints = 2;
        await this._addAction.AddMasterPointAsync(this._player, (ushort)this._skillIdRank1).ConfigureAwait(false);
        this._player.SelectedCharacter.LearnedSkills.First().Level = 10;
        await this._addAction.AddMasterPointAsync(this._player, (ushort)this._skillIdRank2).ConfigureAwait(false);
        this._player.SelectedCharacter.LearnedSkills.Last().Level = 10;
        this._player.SelectedCharacter.MasterLevelUpPoints = 9;
        await this._addAction.AddMasterPointAsync(this._player, (ushort)this._skillIdRank3).ConfigureAwait(false);
        Assert.That(this._player.SelectedCharacter.LearnedSkills, Has.Count.EqualTo(2));
        Assert.That(this._player.SelectedCharacter.LearnedSkills.Any(l => l.Skill == this._skillRank3), Is.False);
    }

    /// <summary>
    /// Tests if adding a point to a new skill results in the skill having level 1.
    /// </summary>
    [Test]
    public async Task AddedSkillGotLevelAsync()
    {
        this._player.SelectedCharacter!.MasterLevelUpPoints = 1;
        await this._addAction.AddMasterPointAsync(this._player, (ushort)this._skillIdRank1).ConfigureAwait(false);
        Assert.That(this._player.SelectedCharacter.LearnedSkills.First().Level, Is.EqualTo(1));
    }

    /// <summary>
    /// Tests if adding a point to a skill increases its level by one.
    /// </summary>
    [Test]
    public async Task AddLevelToLearnedSkillAsync()
    {
        this._player.SelectedCharacter!.MasterLevelUpPoints = 2;
        await this._addAction.AddMasterPointAsync(this._player, (ushort)this._skillIdRank1).ConfigureAwait(false);
        await this._addAction.AddMasterPointAsync(this._player, (ushort)this._skillIdRank1).ConfigureAwait(false);
        Assert.That(this._player.SelectedCharacter.LearnedSkills.First().Level, Is.EqualTo(2));
    }

    /// <summary>
    /// Tests if adding a point to a new skill fails because the required skill is not learned yet.
    /// </summary>
    [Test]
    public async Task RequiredSkillNotLearnedAsync()
    {
        this._player.SelectedCharacter!.MasterLevelUpPoints = 2;
        await this._addAction.AddMasterPointAsync(this._player, (ushort)this._skillIdRank1).ConfigureAwait(false);
        this._player.SelectedCharacter.LearnedSkills.First().Level = 10;

        this._skillRank2.MasterDefinition!.RequiredMasterSkills.Add(new Skill());
        await this._addAction.AddMasterPointAsync(this._player, (ushort)this._skillIdRank2).ConfigureAwait(false);
        Assert.That(this._player.SelectedCharacter.LearnedSkills, Has.Count.EqualTo(1));
        Assert.That(this._player.SelectedCharacter.LearnedSkills.Any(l => l.Skill == this._skillRank2), Is.False);
    }

    /// <summary>
    /// Tests if adding a point to a new skill not fails because the required skill has been learned.
    /// </summary>
    [Test]
    public async Task RequiredSkillLearnedAsync()
    {
        this._player.SelectedCharacter!.MasterLevelUpPoints = 2;
        await this._addAction.AddMasterPointAsync(this._player, (ushort)this._skillIdRank1).ConfigureAwait(false);
        this._player.SelectedCharacter.LearnedSkills.First().Level = 10;
        this._skillRank2.MasterDefinition!.RequiredMasterSkills.Add(this._skillRank1);
        await this._addAction.AddMasterPointAsync(this._player, (ushort)this._skillIdRank2).ConfigureAwait(false);
        Assert.That(this._player.SelectedCharacter.LearnedSkills, Has.Count.EqualTo(2));
        Assert.That(this._player.SelectedCharacter.LearnedSkills.Any(l => l.Skill == this._skillRank2), Is.True);
    }

    /// <summary>
    /// Tests if adding master points decreases the available master points.
    /// </summary>
    [Test]
    public async Task MasterLevelUpPointDecreasedWhenLearnedAsync()
    {
        this._player.SelectedCharacter!.MasterLevelUpPoints = 1;
        await this._addAction.AddMasterPointAsync(this._player, (ushort)this._skillIdRank1).ConfigureAwait(false);
        Assert.That(this._player.SelectedCharacter.MasterLevelUpPoints, Is.EqualTo(0));
    }

    /// <summary>
    /// Tests if a failed adding of master points does not decrease the available master points.
    /// </summary>
    [Test]
    public async Task MasterLevelUpPointNotDecreasedWhenNotLearnedAsync()
    {
        this._player.SelectedCharacter!.MasterLevelUpPoints = 1;
        await this._addAction.AddMasterPointAsync(this._player, (ushort)this._skillIdRank2).ConfigureAwait(false);
        Assert.That(this._player.SelectedCharacter.MasterLevelUpPoints, Is.EqualTo(1));
    }

    /// <summary>
    /// Tests if the adding of master points fails when the maximum level (20) of a skill has been reached.
    /// </summary>
    [Test]
    public async Task MasterLevelMaximumReachedAsync()
    {
        this._player.SelectedCharacter!.MasterLevelUpPoints = 3;
        await this._addAction.AddMasterPointAsync(this._player, (ushort)this._skillIdRank1).ConfigureAwait(false);
        this._player.SelectedCharacter.LearnedSkills.First().Level = 19;
        await this._addAction.AddMasterPointAsync(this._player, (ushort)this._skillIdRank1).ConfigureAwait(false);
        await this._addAction.AddMasterPointAsync(this._player, (ushort)this._skillIdRank1).ConfigureAwait(false);
        Assert.That(this._player.SelectedCharacter.LearnedSkills.First().Level, Is.EqualTo(20));
        Assert.That(this._player.SelectedCharacter.MasterLevelUpPoints, Is.EqualTo(1));
    }

    /// <summary>Tests a class-specific prerequisite with another skill already qualifying in the previous rank.</summary>
    [TestCase(419, 415, 413, 11)]
    [TestCase(533, 529, 526, 17)]
    [TestCase(551, 260, 599, 25)]
    [TestCase(552, 261, 599, 25)]
    public async Task RequiresMatchingSkillAsync(short number, short parentNumber, short otherNumber, byte classNumber)
    {
        var provider = new InMemoryPersistenceContextProvider();
        var initializer = new Persistence.Initialization.VersionSeasonSix.DataInitialization(provider, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        await initializer.CreateInitialDataAsync(1, true).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var skills = configuration.Skills.ToDictionary(s => s.Number);
        var skill = skills[number];
        var parent = skills[parentNumber];
        var character = this._player.SelectedCharacter!;
        character.CharacterClass = skill.QualifiedCharacters.Single(c => c.Number == classNumber);
        character.MasterLevelUpPoints = 1;
        this._player.GameContext.Configuration.Skills.Add(skill);
        character.LearnedSkills.Add(new SkillEntry { Skill = skills[otherNumber], Level = 10 });
        Assert.That(skills[otherNumber].MasterDefinition!.Root, Is.SameAs(skill.MasterDefinition!.Root));
        Assert.That(skills[otherNumber].MasterDefinition!.Rank, Is.EqualTo(skill.MasterDefinition.Rank - 1));

        await this._addAction.AddMasterPointAsync(this._player, (ushort)number).ConfigureAwait(false);
        Assert.That(character.MasterLevelUpPoints, Is.EqualTo(1));
        Assert.That(character.LearnedSkills.Any(s => s.Skill == skill), Is.False);

        await this._player.SkillList!.AddLearnedSkillAsync(parent).ConfigureAwait(false);
        if (parent.MasterDefinition is not null)
        {
            this._player.SkillList.GetSkill((ushort)parentNumber)!.Level = 10;
        }

        await this._addAction.AddMasterPointAsync(this._player, (ushort)number).ConfigureAwait(false);
        Assert.That(character.LearnedSkills.Single(s => s.Skill == skill).Level, Is.EqualTo(1));
        Assert.That(character.MasterLevelUpPoints, Is.Zero);
    }

    /// <summary>Skills sharing a formula may have different level caps.</summary>
    [Test]
    public void SharedFormulaHonorsBothCaps()
    {
        const string formula = "level * 0.54321";
        var definition = new MasterSkillDefinition { MaximumLevel = 10, DisplayValueFormula = formula };
        var entry = new SkillEntry { Level = 10, Skill = new Skill { MasterDefinition = definition } };
        Assert.That(entry.CalculateDisplayValue(), Is.EqualTo(5.4321f).Within(0.0001f));
        definition.MaximumLevel = 20;
        entry.Level = 20;
        Assert.That(entry.CalculateDisplayValue(), Is.EqualTo(10.8642f).Within(0.0001f));
    }

    private MasterSkillRoot GetRoot(byte rootId)
    {
        if (!this._roots.TryGetValue(rootId, out var root))
        {
            root = new MasterSkillRoot();
            this._roots.Add(rootId, root);
        }

        return root;
    }

    /// <summary>Spends a point in Defense Increase using only lightning resistance to qualify at rank three.</summary>
    [Test]
    public async Task BladeMasterResistanceUnlocksDefenseAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        var initializer = new Persistence.Initialization.VersionSeasonSix.DataInitialization(provider, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        await initializer.CreateInitialDataAsync(1, true).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var defense = configuration.Skills.Single(s => s.Number == 309);
        var character = this._player.SelectedCharacter!;
        character.CharacterClass = defense.QualifiedCharacters.Single(c => c.Number == 7);
        character.MasterLevelUpPoints = 47;
        this._player.GameContext.Configuration.Skills.Add(defense);
        var lightning = new SkillEntry { Skill = configuration.Skills.Single(s => s.Number == 308), Level = 9 };
        character.LearnedSkills.Add(lightning);
        character.LearnedSkills.Add(new SkillEntry { Skill = configuration.Skills.Single(s => s.Number == 311), Level = 10 });

        await this._addAction.AddMasterPointAsync(this._player, 309).ConfigureAwait(false);
        Assert.That(character.MasterLevelUpPoints, Is.EqualTo(47));
        Assert.That(character.LearnedSkills.Any(s => s.Skill == defense), Is.False);

        lightning.Level = 10;
        await this._addAction.AddMasterPointAsync(this._player, 309).ConfigureAwait(false);
        Assert.That(character.LearnedSkills.Single(s => s.Skill == defense).Level, Is.EqualTo(1));
        Assert.That(character.MasterLevelUpPoints, Is.EqualTo(46));
    }

    private Skill CreateSkill(short id, byte rank, byte rootId, Skill? requiredSkill, CharacterClass charClass)
    {
        var masterDef = new Mock<MasterSkillDefinition>();
        masterDef.SetupAllProperties();
        masterDef.Object.Rank = rank;
        masterDef.Object.MaximumLevel = 20;
        masterDef.Object.MinimumLevel = 1;
        masterDef.Object.Root = this.GetRoot(rootId);
        masterDef.Setup(m => m.RequiredMasterSkills).Returns(new List<Skill>());
        if (requiredSkill != null)
        {
            masterDef.Object.RequiredMasterSkills.Add(requiredSkill);
        }

        var skill = new Mock<Skill>();
        skill.SetupAllProperties();
        skill.Object.Number = id;
        skill.Setup(s => s.QualifiedCharacters).Returns(new List<CharacterClass>());
        skill.Object.QualifiedCharacters.Add(charClass);
        skill.Object.MasterDefinition = masterDef.Object;

        return skill.Object;
    }
}
