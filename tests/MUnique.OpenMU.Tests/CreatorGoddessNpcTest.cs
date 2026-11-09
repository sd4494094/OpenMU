// <copyright file="CreatorGoddessNpcTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Globalization;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.Views.NPC;

/// <summary>
/// Checks that Aria greets the player without holding a conversation or changing gameplay state.
/// </summary>
[TestFixture]
public class CreatorGoddessNpcTest
{
    /// <summary>Repeated greetings release the NPC session and allow the next merchant interaction.</summary>
    [TestCase("en")]
    [TestCase("zh-CN")]
    public async Task GreetingReleasesSessionAndAllowsAnotherNpcAsync(string culture)
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        player.Culture = CultureInfo.GetCultureInfo(culture);
        var npc = CreateNpc(player, CreatorGoddessNpcPlugIn.NpcNumber);
        var plugin = new CreatorGoddessNpcPlugIn
        {
            Configuration = new NpcTalkPlugInConfiguration { Npc = npc.Definition },
        };
        player.GameContext.PlugInManager.RegisterPlugInAtPlugInPoint<IPlayerTalkToNpcPlugIn>(plugin);
        var position = player.Position;
        var money = player.Money;
        var points = player.SelectedCharacter!.LevelUpPoints;
        var talk = new TalkNpcAction();
        for (var count = 0; count < 2; count++)
        {
            await talk.TalkToNpcAsync(player, npc).ConfigureAwait(false);
            Assert.That(player.OpenedNpc, Is.Null);
            Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.EnteredWorld));
        }

        Mock.Get(player.ViewPlugIns.GetPlugIn<IShowMessageOfObjectPlugIn>()!).Verify(
            view => view.ShowMessageOfObjectAsync("愿创世之光，照亮你的旅途。", npc), Times.Exactly(2));
        Assert.That(player.Position, Is.EqualTo(position));
        Assert.That(player.Money, Is.EqualTo(money));
        Assert.That(player.SelectedCharacter.LevelUpPoints, Is.EqualTo(points));
        var nextNpc = CreateNpc(player, 230);
        nextNpc.Definition.NpcWindow = NpcWindow.Lahap;
        await talk.TalkToNpcAsync(player, nextNpc).ConfigureAwait(false);
        Assert.That(player.OpenedNpc, Is.SameAs(nextNpc));
        Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.NpcDialogOpened));
    }

    /// <summary>Unconfigured and unrelated NPCs cannot be handled by this plugin.</summary>
    [Test]
    public async Task UnconfiguredOrOtherNpcIsIgnoredAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var npc = CreateNpc(player, CreatorGoddessNpcPlugIn.NpcNumber);
        var plugin = new CreatorGoddessNpcPlugIn();
        var unconfigured = new NpcTalkEventArgs();
        await plugin.PlayerTalksToNpcAsync(player, npc, unconfigured).ConfigureAwait(false);
        Assert.That(unconfigured.HasBeenHandled, Is.False);
        plugin.Configuration = new NpcTalkPlugInConfiguration { Npc = npc.Definition };
        var unrelated = new NpcTalkEventArgs();
        await plugin.PlayerTalksToNpcAsync(player, CreateNpc(player, 710), unrelated).ConfigureAwait(false);
        Assert.That(unrelated.HasBeenHandled, Is.False);
    }

    private static NonPlayerCharacter CreateNpc(Player player, short number)
    {
        var spawn = new MonsterSpawnArea { X1 = player.Position.X, X2 = player.Position.X, Y1 = player.Position.Y, Y2 = player.Position.Y };
        var definition = player.PersistenceContext.CreateNew<MonsterDefinition>();
        definition.Number = number;
        definition.ObjectKind = NpcObjectKind.PassiveNpc;
        definition.NpcWindow = NpcWindow.Undefined;
        return new NonPlayerCharacter(spawn, definition, player.CurrentMap!);
    }
}
