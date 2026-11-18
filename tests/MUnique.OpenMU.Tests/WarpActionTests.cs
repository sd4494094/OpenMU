// <copyright file="WarpActionTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Globalization;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
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
}
