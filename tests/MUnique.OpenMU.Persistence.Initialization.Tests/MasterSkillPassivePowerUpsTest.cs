// <copyright file="MasterSkillPassivePowerUpsTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.Persistence.Initialization.Updates;
using MUnique.OpenMU.Persistence.InMemory;

/// <summary>
/// Tests the <see cref="MasterSkillDefinition.PassivePowerUps"/> in the season 6 data.
/// </summary>
[TestFixture]
internal class MasterSkillPassivePowerUpsTest
{
    private const short TwistingSlashMasteryNumber = 332;
    private const short TripleShotMasteryNumber = 418;
    private const short DurabilityReduction1Number = 300;
    private const short DurabilityReduction1FistMasterNumber = 578;

    /// <summary>
    /// Tests that a new season 6 database has the passive power-ups of the master skills.
    /// </summary>
    [Test]
    public async Task NewDatabaseHasPassivePowerUpsAsync()
    {
        var (_, gameConfiguration) = await CreateSeason6ConfigurationAsync().ConfigureAwait(false);

        AssertPassivePowerUps(gameConfiguration);
    }

    /// <summary>
    /// Tests that the update adds the passive power-ups to a database which was created before they existed,
    /// and that applying it twice doesn't add them twice.
    /// </summary>
    [Test]
    public async Task UpdateAddsPassivePowerUpsAsync()
    {
        var (context, gameConfiguration) = await CreateSeason6ConfigurationAsync().ConfigureAwait(false);
        using var _ = context;
        foreach (var masterDefinition in gameConfiguration.Skills.Select(s => s.MasterDefinition).OfType<MasterSkillDefinition>())
        {
            masterDefinition.PassivePowerUps.Clear();
        }

        gameConfiguration.Attributes.Remove(gameConfiguration.Attributes.Single(a => a.Id == Stats.MasterSkillValue.Id));

        var update = new AddMasterSkillPassivePowerUpsPlugIn();
        await update.ApplyUpdateAsync(context, gameConfiguration).ConfigureAwait(false);
        await update.ApplyUpdateAsync(context, gameConfiguration).ConfigureAwait(false);

        AssertPassivePowerUps(gameConfiguration);
    }

    /// <summary>
    /// Tests that the update depends on the update which sets the target attributes it needs.
    /// </summary>
    [Test]
    public void UpdateDependsOnFinishElfMasterTreeUpdate()
    {
        var update = new AddMasterSkillPassivePowerUpsPlugIn();

        Assert.That(update.DependsOn.Select(d => d.Key), Does.Contain(typeof(FinishElfMasterTreePlugIn).GUID));
        Assert.That(update.Version, Is.GreaterThan(1));
        Assert.That(update.UpdatedAt, Is.GreaterThan(update.CreatedAt));
    }

    /// <summary>Checks the resistance path to Defense Increase on new and upgraded Season 6 configurations.</summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task ResistanceRanksUnlockDefenseAsync(bool existingDatabase)
    {
        var (context, configuration) = await CreateSeason6ConfigurationAsync().ConfigureAwait(false);
        using var _ = context;
        var lightning = GetMasterDefinition(configuration, 308);
        var defense = GetMasterDefinition(configuration, 309);
        var ice = GetMasterDefinition(configuration, 311);
        if (existingDatabase)
        {
            lightning.Rank = 2;
            ice.Rank = 2;
            var update = new FixMasterResistanceRanksPlugIn();
            await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
            await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        }

        Assert.That(lightning.Rank, Is.EqualTo(3));
        Assert.That(ice.Rank, Is.EqualTo(4));
        Assert.That(defense.Rank, Is.EqualTo(lightning.Rank + 1));
        Assert.That(defense.Root, Is.SameAs(lightning.Root));
        Assert.That(defense.RequiredMasterSkills, Is.Empty);
        Assert.That(defense.MinimumLevel, Is.EqualTo(1));
    }

    /// <summary>Checks new and upgraded skill trees against the client prerequisite and cap fields.</summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task SeasonSixClassSkillRulesAsync(bool upgrade)
    {
        var (context, configuration) = await CreateSeason6ConfigurationAsync().ConfigureAwait(false);
        using var _ = context;
        var skills = configuration.Skills.ToDictionary(s => s.Number);
        var parents = new (short Skill, short Parent)[] { (419, 415), (533, 529), (551, 260), (552, 261) };
        var mastery = skills[571].MasterDefinition!;
        if (upgrade)
        {
            foreach (var (skill, parent) in parents)
            {
                skills[skill].MasterDefinition!.RequiredMasterSkills.Remove(skills[parent]);
            }

            skills[415].MasterDefinition!.RequiredMasterSkills.Add(skills[30]);
            skills[419].MasterDefinition!.RequiredMasterSkills.Add(skills[30]);
            mastery.MaximumLevel = 20;
            var update = new FixMasterResistanceRanksPlugIn();
            await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
            await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        }

        Assert.That(skills[415].MasterDefinition!.RequiredMasterSkills, Is.Empty);
        foreach (var (skill, parent) in parents)
        {
            Assert.That(skills[skill].MasterDefinition!.RequiredMasterSkills, Is.EquivalentTo(new[] { skills[parent] }));
        }

        Assert.That(mastery.MaximumLevel, Is.EqualTo(10));
    }

    private static void AssertPassivePowerUps(GameConfiguration gameConfiguration)
    {
        Assert.That(gameConfiguration.Attributes.Count(a => a.Id == Stats.MasterSkillValue.Id), Is.EqualTo(1));

        var passiveSkills = gameConfiguration.Skills
            .Where(s => s.SkillType == SkillType.PassiveBoost && s.MasterDefinition?.TargetAttribute is not null)
            .ToList();
        Assert.That(passiveSkills, Is.Not.Empty);
        foreach (var skill in passiveSkills)
        {
            var masterDefinition = skill.MasterDefinition!;
            Assert.That(masterDefinition.PassivePowerUps.Count(p => p.TargetAttribute == masterDefinition.TargetAttribute), Is.EqualTo(1), skill.Name);
        }

        var twistingSlashMastery = GetMasterDefinition(gameConfiguration, TwistingSlashMasteryNumber);
        Assert.That(twistingSlashMastery.PassivePowerUps, Has.Count.EqualTo(1));
        Assert.That(twistingSlashMastery.PassivePowerUps.Single().TargetAttribute, Is.EqualTo(twistingSlashMastery.TargetAttribute));

        var tripleShotMastery = GetMasterDefinition(gameConfiguration, TripleShotMasteryNumber);
        Assert.That(tripleShotMastery.PassivePowerUps, Has.Count.EqualTo(1));
        Assert.That(tripleShotMastery.TargetAttribute!.Id, Is.EqualTo(Stats.TripleShotExtraProjectiles.Id));
        Assert.That(tripleShotMastery.PassivePowerUps.Single().TargetAttribute!.Id, Is.EqualTo(Stats.TripleShotExtraProjectiles.Id));

        foreach (var number in new[] { DurabilityReduction1Number, DurabilityReduction1FistMasterNumber })
        {
            var durabilityReduction = GetMasterDefinition(gameConfiguration, number);
            Assert.That(durabilityReduction.PassivePowerUps, Has.Count.EqualTo(2));
            var factorPowerUp = durabilityReduction.PassivePowerUps.Single(p => p.TargetAttribute == Stats.DurabilityReductionFactor);
            Assert.That(factorPowerUp.Boost!.RelatedValues.Single().InputAttribute, Is.EqualTo(Stats.SkillLevel));
        }
    }

    private static MasterSkillDefinition GetMasterDefinition(GameConfiguration gameConfiguration, short skillNumber)
    {
        return gameConfiguration.Skills.Single(s => s.Number == skillNumber).MasterDefinition!;
    }

    private static async Task<(IContext Context, GameConfiguration GameConfiguration)> CreateSeason6ConfigurationAsync()
    {
        var contextProvider = new InMemoryPersistenceContextProvider();
        var dataInitialization = new VersionSeasonSix.DataInitialization(contextProvider, new NullLoggerFactory());
        await dataInitialization.CreateInitialDataAsync(1, true).ConfigureAwait(false);

        var context = contextProvider.CreateNewContext();
        var gameConfiguration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).First();
        return (context, gameConfiguration);
    }
}
