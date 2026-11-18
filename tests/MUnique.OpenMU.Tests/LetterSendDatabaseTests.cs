// <copyright file="LetterSendDatabaseTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlayerActions.Messenger;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.GameLogic.Views.Messenger;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.EntityFramework;
using MUnique.OpenMU.PlugIns;
using MUnique.OpenMU.Testing;
using StoredLetter = MUnique.OpenMU.Persistence.EntityFramework.Model.LetterHeader;

/// <summary>
/// Checks the letter and its fee across independent PostgreSQL contexts.
/// </summary>
[TestFixture]
[Explicit("Requires a disposable local PostgreSQL cluster.")]
[NonParallelizable]
public class LetterSendDatabaseTests
{
    /// <summary>
    /// Checks the durable balance and inbox at the time the action acknowledges the request.
    /// </summary>
    /// <param name="scenario">The send path to exercise.</param>
    /// <param name="expected">The expected client result.</param>
    /// <returns>The task.</returns>
    [TestCase("normal", LetterSendSuccess.Success)]
    [TestCase("self", LetterSendSuccess.Success)]
    [TestCase("rejected", LetterSendSuccess.TryAgain)]
    [TestCase("missing", LetterSendSuccess.ReceiverNotExists)]
    [TestCase("short", LetterSendSuccess.NotEnoughMoney)]
    public async Task SendCommitsLetterAndFeeTogetherAsync(string scenario, LetterSendSuccess expected)
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(initializeConfiguration: true).ConfigureAwait(false);
        var provider = new PersistenceContextProvider(NullLoggerFactory.Instance, null);
        GameConfiguration configuration;
        using (var context = provider.CreateNewConfigurationContext())
        {
            configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        }

        var fee = configuration.LetterSendPrice;
        Assert.That(fee, Is.Positive);
        var initialMoney = scenario == "short" ? fee - 1 : fee * 2;
        await CreateCharacterAsync("mailfrom", initialMoney).ConfigureAwait(false);
        await CreateCharacterAsync("mailto", 0).ConfigureAwait(false);
        var receiver = scenario switch
        {
            "self" => "mailfrom",
            "missing" => "notfound",
            _ => "mailto",
        };

        var initializer = new MapInitializer(configuration, NullLogger<MapInitializer>.Instance, NullDropGenerator.Instance, null);
        await using var game = new GameContext(configuration, provider, initializer, NullLoggerFactory.Instance,
            new PlugInManager(null, NullLoggerFactory.Instance, null, null), NullDropGenerator.Instance, new ConfigurationChangeMediator());
        initializer.PlugInManager = game.PlugInManager;
        initializer.PathFinderPool = game.PathFinderPool;
        game.StopPeriodicTasks();
        await using var player = new MailPlayer(game);
        var account = await player.PersistenceContext.GetAccountByLoginNameAsync("mailfrom").ConfigureAwait(false);
        await player.SetAccountAsync(account!).ConfigureAwait(false);
        await player.PlayerState.TryAdvanceToAsync(PlayerState.LoginScreen).ConfigureAwait(false);
        await player.PlayerState.TryAdvanceToAsync(PlayerState.Authenticated).ConfigureAwait(false);
        await player.PlayerState.TryAdvanceToAsync(PlayerState.CharacterSelection).ConfigureAwait(false);
        await player.SetSelectedCharacterAsync(account!.Characters.Single()).ConfigureAwait(false);
        Assert.That(player.SelectedCharacter, Is.Not.Null);

        await using var schema = new EntityDataContext();
        if (scenario == "rejected")
        {
            await schema.Database.ExecuteSqlRawAsync(
                """ALTER TABLE data."LetterHeader" ADD CONSTRAINT reject_mail_fixture CHECK ("Subject" <> 'Mail fixture');""").ConfigureAwait(false);
        }

        var success = expected == LetterSendSuccess.Success;
        var result = Mock.Get(player.ViewPlugIns.GetPlugIn<ILetterSendResultPlugIn>()!);
        result.Setup(view => view.LetterSendResultAsync(expected, 17)).Returns(async () =>
            await AssertDurableOutcomeAsync().ConfigureAwait(false));

        await new LetterSendAction().SendLetterAsync(player, receiver, "Fixture body", "Mail fixture", 3, 4, 17).ConfigureAwait(false);
        result.Verify(view => view.LetterSendResultAsync(expected, 17), Times.Once);
        result.VerifyNoOtherCalls();
        Assert.That(player.Money, Is.EqualTo(initialMoney - (success ? fee : 0)));
        Mock.Get(player.ViewPlugIns.GetPlugIn<IAddToLetterListPlugIn>()!)
            .Verify(view => view.AddToLetterListAsync(It.IsAny<LetterHeader>(), 0, true), scenario == "self" ? Times.Once() : Times.Never());

        if (scenario == "rejected")
        {
            await schema.Database.ExecuteSqlRawAsync(
                """ALTER TABLE data."LetterHeader" DROP CONSTRAINT reject_mail_fixture;""").ConfigureAwait(false);
        }

        Assert.That(await player.SaveProgressAsync().ConfigureAwait(false), Is.True);
        await AssertDurableOutcomeAsync().ConfigureAwait(false);
        return;

        async ValueTask CreateCharacterAsync(string login, int money)
        {
            using var context = provider.CreateNewPlayerContext(configuration);
            var owner = context.CreateNew<Account>();
            owner.LoginName = login;
            owner.PasswordHash = "unused-fixture-password";
            var character = context.CreateNew<Character>();
            character.Name = login;
            character.CharacterClass = configuration.CharacterClasses.First(c => c.CanGetCreated && c.HomeMap is not null);
            character.CurrentMap = character.CharacterClass.HomeMap;
            character.CreateDate = DateTime.UtcNow;
            character.KeyConfiguration = new byte[30];
            character.Inventory = context.CreateNew<ItemStorage>();
            character.Inventory.Money = money;
            foreach (var attribute in character.CharacterClass.StatAttributes)
            {
                character.Attributes.Add(context.CreateNew<StatAttribute>(attribute.Attribute, attribute.BaseValue));
            }

            owner.Characters.Add(character);
            Assert.That(await context.SaveChangesAsync().ConfigureAwait(false), Is.True);
        }

        async ValueTask AssertDurableOutcomeAsync()
        {
            using var context = provider.CreateNewPlayerContext(configuration);
            var sender = (await context.GetAccountByLoginNameAsync("mailfrom").ConfigureAwait(false))!.Characters.Single();
            Assert.That(sender.Inventory!.Money, Is.EqualTo(initialMoney - (success ? fee : 0)));
            var headers = await schema.Set<StoredLetter>().AsNoTracking().ToListAsync().ConfigureAwait(false);
            if (!success)
            {
                Assert.That(headers, Is.Empty);
                Assert.That(await schema.Set<Persistence.EntityFramework.Model.LetterBody>().CountAsync().ConfigureAwait(false), Is.Zero);
                return;
            }

            var recipient = (await context.GetAccountByLoginNameAsync(receiver).ConfigureAwait(false))!.Characters.Single();
            var header = headers.Single();
            Assert.That(header.ReceiverId, Is.EqualTo(recipient.GetId()));
            Assert.That(header.SenderName, Is.EqualTo("mailfrom"));
            Assert.That(header.Subject, Is.EqualTo("Mail fixture"));
            var body = await context.GetLetterBodyByHeaderIdAsync(header.Id).ConfigureAwait(false);
            Assert.That(body, Is.Not.Null);
            Assert.That(body!.Message, Is.EqualTo("Fixture body"));
            Assert.That(body.Rotation, Is.EqualTo(3));
            Assert.That(body.Animation, Is.EqualTo(4));
        }
    }

    private sealed class MailPlayer(IGameContext game) : Player(game)
    {
        protected override ICustomPlugInContainer<IViewPlugIn> CreateViewPlugInContainer() => new MockViewPlugInContainer();
    }
}
