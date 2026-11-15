// <copyright file="ChineseCharacterNamesTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.Persistence.Initialization.Updates;
using MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix;
using MUnique.OpenMU.Persistence.InMemory;

/// <summary>Verifies the fresh and existing Season Six character name policies.</summary>
[TestFixture]
[NonParallelizable]
public class ChineseCharacterNamesTest
{
    /// <summary>Fresh initialization installs a compatible Chinese name policy.</summary>
    [Test]
    public async Task FreshConfigurationSupportsChineseNamesAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        using var context = provider.CreateNewContext();
        var config = context.CreateNew<GameConfiguration>();
        new GameConfigurationInitializer(context, config).Initialize();
        await context.SaveChangesAsync().ConfigureAwait(false);
        Assert.That(config.CharacterNameRegex, Is.EqualTo(GameConfigurationInitializer.DefaultCharacterNameRegex));
        Assert.That(config.IsValidCharacterName("血雨星"), Is.True);
        Assert.That(config.IsValidCharacterName("血雨星辰"), Is.False);
    }

    /// <summary>Only the recognized old default is upgraded, and repeats do not duplicate updates.</summary>
    /// <param name="original">The original policy.</param>
    /// <param name="shouldUpgrade">Whether it should be replaced.</param>
    [TestCase("^[a-zA-Z0-9]{3,10}$", true)]
    [TestCase("^[a-z]{5,8}$", false)]
    [TestCase(null, false)]
    public async Task UpgradeIsIdempotentAndPreservesCustomPoliciesAsync(string? original, bool shouldUpgrade)
    {
        var provider = new InMemoryPersistenceContextProvider();
        using var context = provider.CreateNewContext();
        var config = context.CreateNew<GameConfiguration>();
        config.CharacterNameRegex = original;
        var update = new AllowChineseCharacterNamesPlugIn();
        await update.ApplyUpdateAsync(context, config).ConfigureAwait(false);
        await update.ApplyUpdateAsync(context, config).ConfigureAwait(false);
        Assert.That(config.CharacterNameRegex, Is.EqualTo(shouldUpgrade ? GameConfigurationInitializer.DefaultCharacterNameRegex : original));
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Count(entry => entry.Key == update.Key), Is.EqualTo(1));
        Assert.That(update.IsMandatory, Is.False);
        Assert.That(update.Name, Is.Not.Empty);
        Assert.That(update.Description, Is.Not.Empty);
    }

    /// <summary>Default validation and UTF-8 byte boundaries agree with the client.</summary>
    /// <param name="name">The proposed name.</param>
    /// <param name="expectedResult">Whether it is a valid default name.</param>
    [TestCase("血雨星", true)]
    [TestCase("血雨星A", true)]
    [TestCase("Abc", true)]
    [TestCase("1234567890", true)]
    [TestCase("血雨", false)]
    [TestCase("血雨星辰", false)]
    [TestCase("血雨星辰天", false)]
    [TestCase("Name\n", false)]
    [TestCase("Name!", false)]
    [TestCase("血 雨星", false)]
    [TestCase("玩家😀", false)]
    [TestCase("@Abc", false)]
    public void DefaultPolicyMatchesClient(string name, bool expectedResult)
    {
        Assert.That(CharacterInitialization.IsValidCharacterName(name, GameConfigurationInitializer.DefaultCharacterNameRegex), Is.EqualTo(expectedResult));
    }
}
