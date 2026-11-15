// <copyright file="ChineseCharacterCreationTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.GameLogic.PlayerActions.Character;
using MUnique.OpenMU.Network.Packets.ClientToServer;
using MUnique.OpenMU.Network.Packets.ServerToClient;
using MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix;

/// <summary>Exercises player character creation and the unchanged UTF-8 packet fields.</summary>
[TestFixture]
public class ChineseCharacterCreationTests
{
    /// <summary>Creates or rejects a name through the real player action with isolated in-memory persistence.</summary>
    /// <param name="name">The proposed name.</param>
    /// <param name="expectedCount">The expected number of created characters.</param>
    [TestCase("血雨星", 1)]
    [TestCase("血雨星A", 1)]
    [TestCase("Abc", 1)]
    [TestCase("血雨星辰", 0)]
    [TestCase("血雨星辰天", 0)]
    [TestCase("血 雨星", 0)]
    public async Task PlayerCreationUsesChinesePolicyAsync(string name, int expectedCount)
    {
        var fixture = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var configuration = fixture.GameContext.Configuration;
        configuration.CharacterNameRegex = GameConfigurationInitializer.DefaultCharacterNameRegex;
        configuration.MaximumCharactersPerAccount = 5;
        var player = new OfflinePlayer(fixture.GameContext);
        player.Account = player.PersistenceContext.CreateNew<Account>();
        var characterClass = player.PersistenceContext.CreateNew<CharacterClass>();
        characterClass.Number = 0;
        characterClass.CanGetCreated = true;
        characterClass.IsActive = true;
        characterClass.HomeMap = configuration.Maps.First();
        characterClass.HomeMap.IsActive = true;
        configuration.CharacterClasses.Add(characterClass);
        await player.PlayerState.TryAdvanceToAsync(PlayerState.LoginScreen).ConfigureAwait(false);
        await player.PlayerState.TryAdvanceToAsync(PlayerState.Authenticated).ConfigureAwait(false);
        await player.PlayerState.TryAdvanceToAsync(PlayerState.CharacterSelection).ConfigureAwait(false);

        await new CreateCharacterAction().CreateCharacterAsync(player, name, 0).ConfigureAwait(false);

        Assert.That(player.Account.Characters, Has.Count.EqualTo(expectedCount));
        if (expectedCount > 0)
        {
            Assert.That(player.Account.Characters.Single().Name, Is.EqualTo(name));
        }
    }

    /// <summary>Chinese and mixed names survive creation, response, public chat and whisper packets intact.</summary>
    /// <param name="name">A boundary name.</param>
    [TestCase("血雨星")]
    [TestCase("血雨星A")]
    [TestCase("1234567890")]
    public void NamePacketsRoundTripWithoutChangingLayout(string name)
    {
        var create = new CreateCharacterRef(new byte[CreateCharacterRef.Length]);
        create.Name = name;
        Assert.That(create.Name, Is.EqualTo(name));

        var response = new CharacterCreationSuccessfulRef(new byte[CharacterCreationSuccessfulRef.Length]);
        response.CharacterName = name;
        Assert.That(response.CharacterName, Is.EqualTo(name));

        var chat = new PublicChatMessageRef(new byte[64]);
        chat.Character = name;
        Assert.That(chat.Character, Is.EqualTo(name));

        var whisper = new WhisperMessageRef(new byte[64]);
        whisper.ReceiverName = name;
        Assert.That(whisper.ReceiverName, Is.EqualTo(name));

        var displayed = new ChatMessageRef(new byte[64]);
        displayed.Sender = name;
        Assert.That(displayed.Sender, Is.EqualTo(name));
    }
}
