// <copyright file="StatAdvisorTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Globalization;
using Moq;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameLogic.Resets;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.GameLogic.Views.NPC;
using MUnique.OpenMU.PlugIns;
using MonsterDefinition = MUnique.OpenMU.Persistence.BasicModel.MonsterDefinition;
using MonsterSpawnArea = MUnique.OpenMU.Persistence.BasicModel.MonsterSpawnArea;

/// <summary>Verifies the explicit reset confirmation and atomic batch point workflow.</summary>
[TestFixture]
public class StatAdvisorTest
{
    /// <summary>Shows the server quote before charging or refunding points.</summary>
    [Test]
    public async Task ResetPreviewDoesNotChangeAnythingUntilConfirmedAsync()
    {
        var player = await CreatePlayerAsync().ConfigureAwait(false);
        player.Money = 1000;
        player.Attributes![Stats.BaseStrength] = 128;
        player.Attributes[Stats.Level] = 200;
        player.SelectedCharacter!.LevelUpPoints = 7;
        player.GameContext.FeaturePlugIns.AddPlugIn(new StatResetFeaturePlugIn
        {
            Configuration = new StatResetConfiguration { RequiredMoney = 100, MoveHome = false, LogOut = false },
        }, true);
        await OpenNpcAsync(player).ConfigureAwait(false);
        await StatAdvisorAction.HandleAsync(player, 0, string.Empty, string.Empty, new int[5]).ConfigureAwait(false);

        var home = ReadDialog(player);
        Assert.That(home.Page, Is.Zero);
        await StatAdvisorAction.HandleAsync(player, 1, home.SessionId, string.Empty, new int[5]).ConfigureAwait(false);
        var preview = ReadDialog(player);

        Assert.That(preview.Page, Is.EqualTo(1));
        Assert.That(preview.CanConfirm, Is.True);
        Assert.That(preview.Message, Does.Contain("100"));
        Assert.That(player.Money, Is.EqualTo(1000));
        Assert.That(player.Attributes[Stats.BaseStrength], Is.EqualTo(128));
        Assert.That(player.SelectedCharacter.LevelUpPoints, Is.EqualTo(7));

        await StatAdvisorAction.HandleAsync(player, 2, preview.SessionId, preview.Token, new int[5]).ConfigureAwait(false);
        Assert.That(player.Money, Is.EqualTo(900));
        Assert.That(player.Attributes[Stats.BaseStrength], Is.EqualTo(28));
        Assert.That(player.SelectedCharacter.LevelUpPoints, Is.EqualTo(107));
        Assert.That(player.OpenedNpc, Is.Null);
    }

    /// <summary>Returning from a reset quote leaves the character and its costs unchanged.</summary>
    [Test]
    public async Task ResetPreviewCanBeCancelledWithoutApplyingAsync()
    {
        var player = await CreatePlayerAsync().ConfigureAwait(false);
        player.Money = 1000;
        player.Attributes![Stats.BaseStrength] = 128;
        player.GameContext.FeaturePlugIns.AddPlugIn(new StatResetFeaturePlugIn
        {
            Configuration = new StatResetConfiguration { RequiredMoney = 100, MoveHome = false, LogOut = false },
        }, true);
        await OpenNpcAsync(player).ConfigureAwait(false);
        await StatAdvisorAction.HandleAsync(player, 0, string.Empty, string.Empty, new int[5]).ConfigureAwait(false);
        var home = ReadDialog(player);
        await StatAdvisorAction.HandleAsync(player, 1, home.SessionId, string.Empty, new int[5]).ConfigureAwait(false);
        var quote = ReadDialog(player);
        await StatAdvisorAction.HandleAsync(player, 6, home.SessionId, quote.Token, new int[5]).ConfigureAwait(false);

        Assert.That(player.Money, Is.EqualTo(1000));
        Assert.That(player.Attributes[Stats.BaseStrength], Is.EqualTo(128));
        Assert.That(player.SelectedCharacter!.LevelUpPoints, Is.EqualTo(7));
    }

    /// <summary>Applies a confirmed allocation once across every supported base attribute.</summary>
    [Test]
    public async Task BatchAllocationAppliesAllAttributesAndConsumesOnlyNormalPointsOnceAsync()
    {
        var player = await CreatePlayerAsync().ConfigureAwait(false);
        await OpenNpcAsync(player).ConfigureAwait(false);
        await StatAdvisorAction.HandleAsync(player, 0, string.Empty, string.Empty, new int[5]).ConfigureAwait(false);
        var home = ReadDialog(player);

        await StatAdvisorAction.HandleAsync(player, 3, home.SessionId, string.Empty, new int[5]).ConfigureAwait(false);
        var form = ReadDialog(player);
        Assert.That(form.Stats, Has.Count.EqualTo(4));

        int[] amounts = [2, 3, 0, 2, 0];
        await StatAdvisorAction.HandleAsync(player, 4, home.SessionId, string.Empty, amounts).ConfigureAwait(false);
        var preview = ReadDialog(player);
        Assert.That(preview.Page, Is.EqualTo(3));
        Assert.That(preview.CanConfirm, Is.True);
        Assert.That(preview.Stats.Single(s => s.Id == 0).Amount, Is.EqualTo(2));
        Assert.That(player.SelectedCharacter!.LevelUpPoints, Is.EqualTo(7));

        await StatAdvisorAction.HandleAsync(player, 5, home.SessionId, preview.Token, new int[5]).ConfigureAwait(false);
        await StatAdvisorAction.HandleAsync(player, 5, home.SessionId, preview.Token, new int[5]).ConfigureAwait(false);

        Assert.That(player.Attributes![Stats.BaseStrength], Is.EqualTo(30));
        Assert.That(player.Attributes[Stats.BaseAgility], Is.EqualTo(23));
        Assert.That(player.Attributes[Stats.BaseVitality], Is.EqualTo(25));
        Assert.That(player.Attributes[Stats.BaseEnergy], Is.EqualTo(12));
        Assert.That(player.SelectedCharacter.LevelUpPoints, Is.Zero);
        Assert.That(ReadDialog(player).ClearInputs, Is.True);
    }

    /// <summary>Rejects unsupported, negative, empty and over-budget allocations.</summary>
    [TestCase(new[] { 0, 0, 0, 0, 0 })]
    [TestCase(new[] { 8, 0, 0, 0, 0 })]
    [TestCase(new[] { -1, 1, 0, 0, 0 })]
    [TestCase(new[] { 0, 0, 0, 0, 1 })]
    public async Task InvalidBatchPreviewCannotBeConfirmedAsync(int[] amounts)
    {
        var player = await CreatePlayerAsync().ConfigureAwait(false);
        await OpenNpcAsync(player).ConfigureAwait(false);
        await StatAdvisorAction.HandleAsync(player, 0, string.Empty, string.Empty, new int[5]).ConfigureAwait(false);
        var home = ReadDialog(player);

        await StatAdvisorAction.HandleAsync(player, 4, home.SessionId, string.Empty, amounts).ConfigureAwait(false);
        var response = ReadDialog(player);
        Assert.That(response.Page, Is.EqualTo(2));
        Assert.That(response.CanConfirm, Is.False);
        Assert.That(player.SelectedCharacter!.LevelUpPoints, Is.EqualTo(7));
        Assert.That(player.Attributes![Stats.BaseStrength], Is.EqualTo(28));
    }

    /// <summary>Includes leadership only when the current class can allocate it.</summary>
    [Test]
    public async Task LeadershipIsShownOnlyForClassesThatCanIncreaseItAsync()
    {
        var gameContext = GameContextTestHelper.CreateGameContext();
        var player = new TestPlayer(gameContext);
        var character = PlayerTestHelper.CreateCharacter(gameContext);
        character.CharacterClass!.StatAttributes.Add(new StatAttributeDefinition(Stats.BaseLeadership, 0, true));
        await PlayerTestHelper.EnterWorldAsync(player, character).ConfigureAwait(false);
        player.Attributes![Stats.BaseLeadership] = 50;
        player.Culture = CultureInfo.GetCultureInfo("en");
        player.Attributes![Stats.Level] = 200;
        player.SelectedCharacter!.LevelUpPoints = 7;
        player.CurrentMap!.Terrain.SafezoneMap[player.Position.X, player.Position.Y] = true;
        await OpenNpcAsync(player).ConfigureAwait(false);
        await StatAdvisorAction.HandleAsync(player, 0, string.Empty, string.Empty, new int[5]).ConfigureAwait(false);
        var home = ReadDialog(player);

        await StatAdvisorAction.HandleAsync(player, 3, home.SessionId, string.Empty, new int[5]).ConfigureAwait(false);
        Assert.That(ReadDialog(player).Stats, Has.Count.EqualTo(5));
        await StatAdvisorAction.HandleAsync(player, 4, home.SessionId, string.Empty, [0, 0, 0, 0, 5]).ConfigureAwait(false);
        var preview = ReadDialog(player);
        Assert.That(preview.CanConfirm, Is.True);
        await StatAdvisorAction.HandleAsync(player, 5, home.SessionId, preview.Token, new int[5]).ConfigureAwait(false);
        Assert.That(player.Attributes[Stats.BaseLeadership], Is.EqualTo(55));
        Assert.That(player.SelectedCharacter.LevelUpPoints, Is.EqualTo(2));
    }

    /// <summary>Invalidates stale previews and closed NPC sessions.</summary>
    [Test]
    public async Task StateChangeInvalidatesAllocationQuoteAndCloseInvalidatesSessionAsync()
    {
        var player = await CreatePlayerAsync().ConfigureAwait(false);
        await OpenNpcAsync(player).ConfigureAwait(false);
        await StatAdvisorAction.HandleAsync(player, 0, string.Empty, string.Empty, new int[5]).ConfigureAwait(false);
        var home = ReadDialog(player);
        await StatAdvisorAction.HandleAsync(player, 4, home.SessionId, string.Empty, [1, 0, 0, 0, 0]).ConfigureAwait(false);
        var preview = ReadDialog(player);

        player.SelectedCharacter!.LevelUpPoints--;
        await StatAdvisorAction.HandleAsync(player, 5, home.SessionId, preview.Token, new int[5]).ConfigureAwait(false);
        Assert.That(player.Attributes![Stats.BaseStrength], Is.EqualTo(28));
        Assert.That(ReadDialog(player).CanConfirm, Is.False);

        await new CloseNpcDialogAction().CloseNpcDialogAsync(player).ConfigureAwait(false);
        await OpenNpcAsync(player).ConfigureAwait(false);
        await StatAdvisorAction.HandleAsync(player, 5, home.SessionId, preview.Token, new int[5]).ConfigureAwait(false);
        Assert.That(player.Attributes[Stats.BaseStrength], Is.EqualTo(28));
    }

    private static StatAdvisorDialog ReadDialog(Player player)
    {
        var invocation = Mock.Get(player.ViewPlugIns.GetPlugIn<IShowStatAdvisorPlugIn>()!).Invocations.Last();
        return (StatAdvisorDialog)invocation.Arguments[0];
    }

    private static async ValueTask<Player> CreatePlayerAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        player.Culture = CultureInfo.GetCultureInfo("en");
        player.Attributes![Stats.Level] = 200;
        player.SelectedCharacter!.LevelUpPoints = 7;
        player.CurrentMap!.Terrain.SafezoneMap[player.Position.X, player.Position.Y] = true;
        return player;
    }

    private static async ValueTask OpenNpcAsync(Player player)
    {
        var spawn = new MonsterSpawnArea
        {
            X1 = player.Position.X,
            X2 = player.Position.X,
            Y1 = player.Position.Y,
            Y2 = player.Position.Y,
        };
        var definition = player.PersistenceContext.CreateNew<MonsterDefinition>();
        definition.Number = 464;
        definition.ObjectKind = MUnique.OpenMU.DataModel.Configuration.NpcObjectKind.PassiveNpc;
        definition.NpcWindow = MUnique.OpenMU.DataModel.Configuration.NpcWindow.StatReInitializer;
        var npc = new NonPlayerCharacter(spawn, definition, player.CurrentMap!);
        await new TalkNpcAction().TalkToNpcAsync(player, npc).ConfigureAwait(false);
    }
    
    private sealed class TestPlayer(IGameContext gameContext) : Player(gameContext)
    {
        protected override ICustomPlugInContainer<IViewPlugIn> CreateViewPlugInContainer() => new MockViewPlugInContainer();
    }

}
