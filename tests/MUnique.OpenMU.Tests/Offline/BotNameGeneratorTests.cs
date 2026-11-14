// <copyright file="BotNameGeneratorTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests.Offline;

using System.Globalization;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.Network.Packets.ServerToClient;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.InMemory;

/// <summary>Verifies the finite reviewed inventory and persistent new-character allocation.</summary>
[TestFixture]
public class BotNameGeneratorTests
{
    /// <summary>Default and legacy settings resolve the same reviewed inventory.</summary>
    [Test]
    public void DefaultInventoryHasFiveHundredReviewedEntries()
    {
        var names = BotNameCatalog.Validate(null);
        Assert.That(names, Has.Length.EqualTo(500));
        Assert.That(names.Count(name => name.Any(character => character > 127)), Is.EqualTo(475));
        Assert.That(names.Distinct(StringComparer.OrdinalIgnoreCase).Count(), Is.EqualTo(500));
        Assert.That(names.All(BotNameCatalog.IsValid), Is.True);
        Assert.That(names.Count(name => BotBuild.GetVariant(name) == 0), Is.EqualTo(246));
        Assert.That(names.Count(name => BotBuild.GetVariant(name) == 1), Is.EqualTo(254));
        Assert.That(JsonSerializer.Deserialize<BotConfiguration>("{}")!.CharacterNames, Is.Empty);
        Assert.That(BotNameCatalog.Validate([]), Is.EqualTo(names));
        var custom = JsonSerializer.Deserialize<BotConfiguration>("{\"CharacterNames\":\"阿川, River\\n晚点睡\"}")!;
        Assert.That(custom.ParseCharacterNames(), Is.EqualTo(new[] { "阿川", "River", "晚点睡" }));
        Assert.That(JsonSerializer.Deserialize<BotConfiguration>(JsonSerializer.Serialize(custom))!.ParseCharacterNames(), Is.EqualTo(custom.ParseCharacterNames()));
        Assert.That(BotNameCatalog.Validate(new BotConfiguration().ParseCharacterNames()), Is.EqualTo(names));
    }

    /// <summary>Checks protocol boundaries and rejects administrative or invisible names.</summary>
    /// <param name="name">The nickname.</param>
    /// <param name="valid">Whether it fits the policy.</param>
    [TestCase("阿川", true)]
    [TestCase("晚点睡", true)]
    [TestCase("晚点睡7", true)]
    [TestCase("小北88", true)]
    [TestCase("River", true)]
    [TestCase("ABCDEFGHIJ", true)]
    [TestCase("手有点冷", false)]
    [TestCase("晚点睡77", false)]
    [TestCase("ABCDEFGHIJK", false)]
    [TestCase("A", false)]
    [TestCase("", false)]
    [TestCase(" 阿川", false)]
    [TestCase("阿 川", false)]
    [TestCase("阿川\u200b", false)]
    [TestCase("阿川😀", false)]
    [TestCase("ＧＭ", false)]
    [TestCase("GM阿川", false)]
    [TestCase("Admin", false)]
    [TestCase("客服7", false)]
    [TestCase("艾莉雅", false)]
    public void NamePolicyUsesUtf8Bytes(string name, bool valid)
        => Assert.That(BotNameCatalog.IsValid(name), Is.EqualTo(valid));

    /// <summary>Rejects the entire bad inventory instead of quietly repairing or inventing names.</summary>
    [Test]
    public void InvalidOrCaseDuplicateInventoryIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => new BotNameGenerator(["River", "river"]));
        Assert.Throws<InvalidOperationException>(() => new BotNameGenerator(["阿川", "手有点冷"]));
        Assert.Throws<InvalidOperationException>(() => new BotNameGenerator(["管理员"]));
        Assert.Throws<InvalidOperationException>(() => new BotNameGenerator([null!]));
    }

    /// <summary>All approved names round-trip through existing character and whisper packets.</summary>
    [Test]
    public void ReviewedNamesRoundTripWithoutChangingPackets()
    {
        foreach (var name in BotNameCatalog.DefaultNames.Concat(["晚点睡7", "ABCDEFGHIJ"]))
        {
            var creation = new CharacterCreationSuccessful(new byte[CharacterCreationSuccessful.Length]);
            creation.CharacterName = name;
            var whisper = new ChatMessage(new byte[ChatMessage.GetRequiredSize("hello")]);
            whisper.Type = ChatMessage.ChatMessageType.Whisper;
            whisper.Sender = name;
            Assert.That(creation.CharacterName, Is.EqualTo(name), name);
            Assert.That(whisper.Sender, Is.EqualTo(name), name);
        }
    }

    /// <summary>Allocates only approved names, avoids persisted names and stops without fallback.</summary>
    [Test]
    public async ValueTask ExhaustionNeverGeneratesAnUnreviewedNameAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        using var context = provider.CreateNewPlayerContext(new MUnique.OpenMU.Persistence.BasicModel.GameConfiguration());
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var generator = new BotNameGenerator(["阿川", "晚点睡", "River"]);
        var account = context.CreateNew<Account>();
        account.LoginName = "existing";
        var character = context.CreateNew<Character>();
        character.Name = "阿川";
        account.Characters.Add(character);
        await context.SaveChangesAsync().ConfigureAwait(false);
        var first = await generator.GenerateUniqueAsync(context, reserved).ConfigureAwait(false);
        var second = await generator.GenerateUniqueAsync(context, reserved).ConfigureAwait(false);
        Assert.That(new[] { first, second }, Is.EquivalentTo(new[] { "晚点睡", "River" }));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await generator.GenerateUniqueAsync(context, reserved).ConfigureAwait(false));
        Assert.That(character.Name, Is.EqualTo("阿川"));
    }

    /// <summary>Preserves the requested elf build without consuming names of the other variant.</summary>
    [Test]
    public async ValueTask BuildSelectionDoesNotUseAnIncorrectFallbackAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        using var context = provider.CreateNewPlayerContext(new MUnique.OpenMU.Persistence.BasicModel.GameConfiguration());
        var names = BotNameCatalog.DefaultNames.GroupBy(BotBuild.GetVariant).ToDictionary(group => group.Key, group => group.First());
        var generator = new BotNameGenerator(names.Values);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.That(await generator.GenerateUniqueAsync(context, reserved, 1).ConfigureAwait(false), Is.EqualTo(names[1]));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await generator.GenerateUniqueAsync(context, reserved, 1).ConfigureAwait(false));
        Assert.That(await generator.GenerateUniqueAsync(context, reserved, 0).ConfigureAwait(false), Is.EqualTo(names[0]));
    }

    /// <summary>A separate generation pass still respects previously reserved names and cancellation.</summary>
    [Test]
    public async ValueTask ReservedNamesAndCancellationAreHonoredAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        using var context = provider.CreateNewPlayerContext(new MUnique.OpenMU.Persistence.BasicModel.GameConfiguration());
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "RIVER" };
        var generator = new BotNameGenerator(["River", "阿川"]);
        Assert.That(await generator.GenerateUniqueAsync(context, reserved).ConfigureAwait(false), Is.EqualTo("阿川"));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await generator.GenerateUniqueAsync(context, reserved).ConfigureAwait(false));
        Assert.ThrowsAsync<OperationCanceledException>(async () => await new BotNameGenerator().GenerateUniqueAsync(context, reserved, cancellationToken: new CancellationToken(true)).ConfigureAwait(false));
    }

    /// <summary>Generation persists new names and a later run keeps existing names and build choices.</summary>
    [Test]
    public async ValueTask NewPopulationUsesTheInventoryAndKeepsExistingCharactersAsync()
    {
        var game = CreateGenerationContext();
        var generator = new BotGenerator(game, NullLogger.Instance);
        Assert.That(await generator.EnsureBotsAsync(100, 5, BotStartupProfile.For(true)).ConfigureAwait(false), Is.EqualTo(100));
        using var context = game.PersistenceContextProvider.CreateNewPlayerContext(game.Configuration);
        var accounts = (await context.GetAccountsOrderedByLoginNameAsync(0, 200).ConfigureAwait(false)).ToArray();
        var characters = accounts.SelectMany(account => account.Characters).ToArray();
        Assert.That(characters, Has.Length.EqualTo(500));
        Assert.That(accounts.All(account => account.IsBot), Is.True);
        Assert.That(characters.All(character => BotNameCatalog.DefaultNames.Contains(character.Name)), Is.True);
        var snapshots = characters.Select(character => (character.GetId(), character.Name, Variant: BotBuild.GetVariant(character.Name), Vitality: BotProgression.GetVitalityTarget(character.Name))).ToArray();
        Assert.That(await new BotGenerator(game, NullLogger.Instance).EnsureBotsAsync(100, 5, BotStartupProfile.For(true)).ConfigureAwait(false), Is.Zero);
        Assert.That(characters.Select(character => (character.GetId(), character.Name, Variant: BotBuild.GetVariant(character.Name), Vitality: BotProgression.GetVitalityTarget(character.Name))), Is.EqualTo(snapshots));
    }

    /// <summary>An account whose full name allocation cannot succeed is never attached or saved.</summary>
    [Test]
    public async ValueTask InsufficientInventoryDoesNotLeaveHalfAnAccountAsync()
    {
        var game = CreateGenerationContext();
        var plugIn = game.FeaturePlugIns.GetPlugIn<BotFeaturePlugIn>()!;
        plugIn.Configuration = new BotConfiguration { CharacterNames = "阿川" };
        Assert.That(BotFeaturePlugIn.GetConfiguration(game)!.ParseCharacterNames(), Is.EqualTo(new[] { "阿川" }));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await new BotGenerator(game, NullLogger.Instance).EnsureBotsAsync(1, 2, BotStartupProfile.For(true)).ConfigureAwait(false));
        using var context = game.PersistenceContextProvider.CreateNewPlayerContext(game.Configuration);
        Assert.That(await context.GetAccountByLoginNameAsync("bot0001").ConfigureAwait(false), Is.Null);
    }

    /// <summary>Inventory settings expose localized labels in the Chinese admin panel.</summary>
    [Test]
    public void InventoryCaptionIsLocalized()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            Assert.That(MUnique.OpenMU.GameLogic.Properties.BotNamingResources.CharacterNamesCaption, Is.EqualTo("已审核角色昵称库"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private static IGameContext CreateGenerationContext()
    {
        var game = GameContextTestHelper.CreateGameContext(maximumLevel: 400);
        game.Configuration.MaximumCharactersPerAccount = 5;
        var map = game.Configuration.Maps.First();
        map.IsActive = true;
        using var context = game.PersistenceContextProvider.CreateNewContext();
        var characterClass = context.CreateNew<CharacterClass>();
        characterClass.Number = 0;
        characterClass.CanGetCreated = true;
        characterClass.IsActive = true;
        characterClass.HomeMap = map;
        var level = context.CreateNew<StatAttributeDefinition>();
        level.Attribute = Stats.Level;
        level.BaseValue = 1;
        characterClass.StatAttributes.Add(level);
        var points = context.CreateNew<StatAttributeDefinition>();
        points.Attribute = Stats.PointsPerLevelUp;
        points.BaseValue = 5;
        characterClass.StatAttributes.Add(points);
        game.Configuration.CharacterClasses.Add(characterClass);
        return game;
    }
}
