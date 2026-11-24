// <copyright file="WarpActionTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Globalization;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.ItemCrafting;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.Interfaces;

/// <summary>
/// Verifies the localized response and player state when a warp is rejected.
/// </summary>
[TestFixture]
public class WarpActionTests
{
    /// <summary>
    /// A rejected warp leaves the position and money unchanged and sends one blue message.
    /// </summary>
    [TestCase("zh-CN", true, 0, 2_000, "传送需要2000金币。")]
    [TestCase("zh-CN", true, 100, 0, "达到100级后才能传送。")]
    [TestCase("zh-CN", false, 0, 0, "传送目的地暂时不可用。")]
    [TestCase("en", true, 0, 2_000, "传送需要2000金币。")]
    [TestCase("", true, 0, 2_000, "You need 2000 Zen in order to warp.")]
    public async Task RejectedWarpKeepsPlayerStateAndShowsLocalizedMessageAsync(
        string culture,
        bool hasDestination,
        int requiredLevel,
        int costs,
        string expected)
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        player.Culture = CultureInfo.GetCultureInfo(culture);
        player.Money = 1_999;
        var map = player.CurrentMap!;
        map.Definition.IsActive = true;
        var position = player.Position;
        var warp = new WarpInfo
        {
            Gate = hasDestination ? new ExitGate { Map = map.Definition } : null,
            LevelRequirement = requiredLevel,
            Costs = costs,
        };

        await new WarpAction().WarpToAsync(player, warp).ConfigureAwait(false);

        Assert.That(player.CurrentMap, Is.SameAs(map));
        Assert.That(player.Position, Is.EqualTo(position));
        Assert.That(player.Money, Is.EqualTo(1_999));
        var messages = Mock.Get(player.ViewPlugIns.GetPlugIn<IShowMessagePlugIn>()!);
        messages.Verify(view => view.ShowMessageAsync(expected, MessageType.BlueNormal), Times.Once);
    }

    /// <summary>
    /// Verifies that a valid warp closes the generic NPC dialog before the map changes.
    /// </summary>
    /// <param name="destinationActive">Whether the destination accepts the warp.</param>
    [TestCase(true)]
    [TestCase(false)]
    public async Task WarpClosesGenericNpcDialogOnlyWhenDestinationIsActiveAsync(bool destinationActive)
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var source = player.CurrentMap!;
        var definition = new Mock<MonsterDefinition>();
        definition.SetupGet(npc => npc.ItemCraftings).Returns(new List<ItemCrafting>());
        definition.Object.NpcWindow = NpcWindow.NpcDialog;
        var npc = new NonPlayerCharacter(new MonsterSpawnArea(), definition.Object, source);
        await new TalkNpcAction().TalkToNpcAsync(player, npc).ConfigureAwait(false);
        Assert.That(player.OpenedNpc, Is.SameAs(npc));
        Assert.That(player.PlayerState.CurrentState, Is.SameAs(PlayerState.NpcDialogOpened));

        var destination = new GameMapDefinition { Number = 2, IsActive = destinationActive };

        await player.WarpToAsync(new ExitGate { Map = destination }).ConfigureAwait(false);

        if (destinationActive)
        {
            Assert.That(player.OpenedNpc, Is.Null);
            Assert.That(player.PlayerState.CurrentState, Is.SameAs(PlayerState.ChangingMap));
            Assert.That(player.SelectedCharacter!.CurrentMap, Is.SameAs(destination));
        }
        else
        {
            Assert.That(player.OpenedNpc, Is.SameAs(npc));
            Assert.That(player.PlayerState.CurrentState, Is.SameAs(PlayerState.NpcDialogOpened));
            Assert.That(player.CurrentMap, Is.SameAs(source));
        }
    }
}
