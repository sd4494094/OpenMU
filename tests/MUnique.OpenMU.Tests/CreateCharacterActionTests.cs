// <copyright file="CreateCharacterActionTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Moq;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.CashShop;
using MUnique.OpenMU.GameLogic.PlayerActions.Character;
using MUnique.OpenMU.GameLogic.Views.Character;
using MUnique.OpenMU.Persistence.BasicModel;
using EntityCharacter = MUnique.OpenMU.DataModel.Entities.Character;
using CashShopUseResult = MUnique.OpenMU.GameLogic.Views.CashShop.CashShopUseResult;

/// <summary>
/// Tests that character creation enforces the account's class unlocks independently of the client UI.
/// </summary>
[TestFixture]
public class CreateCharacterActionTests
{
    /// <summary>
    /// A restricted class requires its own unlock, even if another class with the same flag is unlocked.
    /// </summary>
    /// <param name="number">The requested class number.</param>
    /// <param name="flag">The configured client unlock flag.</param>
    /// <param name="hasOtherUnlock">Whether a different class is unlocked.</param>
    [TestCase(12, 4, false)]
    [TestCase(16, 2, false)]
    [TestCase(20, 1, false)]
    [TestCase(24, 8, false)]
    [TestCase(12, 4, true)]
    [TestCase(16, 2, true)]
    [TestCase(20, 1, true)]
    [TestCase(24, 8, true)]
    public async Task LockedClassIsNotCreatedAsync(byte number, byte flag, bool hasOtherUnlock)
    {
        var (player, characterClass) = await CreatePlayerAsync(number, flag).ConfigureAwait(false);
        if (hasOtherUnlock)
        {
            player.Account!.UnlockedCharacterClasses.Add(new CharacterClass { Number = 100, CreationAllowedFlag = flag });
        }

        await new CreateCharacterAction().CreateCharacterAsync(player, "NewHero", characterClass.Number).ConfigureAwait(false);

        Assert.That(player.Account!.Characters, Is.Empty);
        Assert.That(await player.PersistenceContext.GetAsync<EntityCharacter>().ConfigureAwait(false), Is.Empty);
        Mock.Get(player.ViewPlugIns.GetPlugIn<IShowCharacterCreationFailedPlugIn>()!).Verify(p => p.ShowCharacterCreationFailedAsync(), Times.Once);
        Mock.Get(player.ViewPlugIns.GetPlugIn<IShowCreatedCharacterPlugIn>()!).Verify(p => p.ShowCreatedCharacterAsync(It.IsAny<EntityCharacter>()), Times.Never);
    }

    /// <summary>
    /// A persisted unlock may be represented by another configuration object with the same class number.
    /// </summary>
    /// <param name="number">The requested class number.</param>
    /// <param name="flag">The configured client unlock flag.</param>
    [TestCase(12, 4)]
    [TestCase(16, 2)]
    [TestCase(20, 1)]
    [TestCase(24, 8)]
    public async Task MatchingUnlockAllowsCreationAsync(byte number, byte flag)
    {
        var (player, characterClass) = await CreatePlayerAsync(number, flag).ConfigureAwait(false);
        player.Account!.UnlockedCharacterClasses.Add(new CharacterClass { Number = number });

        await new CreateCharacterAction().CreateCharacterAsync(player, "NewHero", number).ConfigureAwait(false);

        Assert.That(player.Account.Characters.Single().CharacterClass, Is.SameAs(characterClass));
        Mock.Get(player.ViewPlugIns.GetPlugIn<IShowCreatedCharacterPlugIn>()!).Verify(p => p.ShowCreatedCharacterAsync(It.IsAny<EntityCharacter>()), Times.Once);
    }

    /// <summary>
    /// Classes without a configured unlock flag remain available to new accounts.
    /// </summary>
    /// <param name="number">The requested base class number.</param>
    [TestCase(0)]
    [TestCase(4)]
    [TestCase(8)]
    public async Task BaseClassDoesNotRequireUnlockAsync(byte number)
    {
        var (player, characterClass) = await CreatePlayerAsync(number, 0).ConfigureAwait(false);

        await new CreateCharacterAction().CreateCharacterAsync(player, "NewHero", number).ConfigureAwait(false);

        Assert.That(player.Account!.Characters.Single().CharacterClass, Is.SameAs(characterClass));
    }

    /// <summary>
    /// A card's delivery enables creation without requiring a high-level sibling character.
    /// </summary>
    /// <param name="number">The class unlocked by the card.</param>
    /// <param name="flag">The configured client unlock flag.</param>
    [TestCase(20, 1)]
    [TestCase(24, 8)]
    public async Task CharacterCardEnablesCreationAsync(byte number, byte flag)
    {
        var (player, characterClass) = await CreatePlayerAsync(number, flag).ConfigureAwait(false);
        characterClass.LevelRequirementByCreation = 150;
        CharacterCardDeliveryPlugInBase card = number == 20
            ? new SummonerCharacterCardDeliveryPlugIn()
            : new RageFighterCharacterCardDeliveryPlugIn();

        Assert.That(await card.DeliverAsync(player, new CashShopProduct()).ConfigureAwait(false), Is.EqualTo(CashShopUseResult.Success));
        await new CreateCharacterAction().CreateCharacterAsync(player, "NewHero", number).ConfigureAwait(false);

        Assert.That(player.Account!.Characters.Single().CharacterClass, Is.SameAs(characterClass));
    }

    private static async ValueTask<(Player Player, CharacterClass Class)> CreatePlayerAsync(byte number, byte flag)
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        await player.SetSelectedCharacterAsync(null).ConfigureAwait(false);
        await player.PlayerState.TryAdvanceToAsync(PlayerState.CharacterSelection).ConfigureAwait(false);
        Mock.Get(player.Account!).Setup(a => a.Characters).Returns(new List<EntityCharacter>());
        var characterClass = new CharacterClass
        {
            Number = number,
            CanGetCreated = true,
            CreationAllowedFlag = flag,
            HomeMap = player.GameContext.Configuration.Maps.Single(),
        };
        player.GameContext.Configuration.CharacterClasses.Add(characterClass);
        player.GameContext.Configuration.MaximumCharactersPerAccount = 5;
        return (player, characterClass);
    }
}
