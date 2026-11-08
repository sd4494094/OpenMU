// <copyright file="ResetNpcDialogTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Globalization;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameLogic.PlayerActions.Chat;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.PlugIns.ChatCommands;
using MUnique.OpenMU.GameLogic.Resets;
using MUnique.OpenMU.GameLogic.Views.NPC;

/// <summary>
/// Verifies read-only queries, explicit confirmation, and stale or forged NPC requests.
/// </summary>
[TestFixture]
public class ResetNpcDialogTest
{
    /// <summary>
    /// Talking, querying and applying never reset or charge the player.
    /// </summary>
    [Test]
    public async Task OpeningAndQueryingOnlyShowCurrentRequirementsAsync()
    {
        var player = await CreatePlayerAsync().ConfigureAwait(false);
        var before = player.Money;
        await new ResetInfoChatCommandPlugIn().HandleCommandAsync(player, "/resetinfo").ConfigureAwait(false);
        var view = Mock.Get(player.ViewPlugIns.GetPlugIn<IShowResetDialogPlugIn>()!);
        view.Verify(v => v.ShowResetDialogAsync("", It.IsAny<string>(),
            It.Is<string>(text => text.Contains("400") && text.Contains("100") && text.Contains("1000")),
            "查询转生条件", "申请转生", "离开"), Times.AtLeastOnce);

        await new ResetChatCommandPlugIn().HandleCommandAsync(player, "/reset prepare").ConfigureAwait(false);
        Assert.That(player.Level, Is.EqualTo(400));
        Assert.That(player.Money, Is.EqualTo(before));
        Assert.That(player.Attributes![Stats.Resets], Is.Zero);
        Assert.That(GetToken(player), Has.Length.EqualTo(32));
    }

    /// <summary>
    /// A valid confirmation charges and resets once, even when repeated.
    /// </summary>
    [Test]
    public async Task ExplicitConfirmationResetsExactlyOnceAsync()
    {
        var player = await CreatePlayerAsync().ConfigureAwait(false);
        var command = new ResetChatCommandPlugIn();
        await command.HandleCommandAsync(player, "/reset prepare").ConfigureAwait(false);
        var token = GetToken(player);
        await command.HandleCommandAsync(player, "/reset confirm " + token).ConfigureAwait(false);
        await command.HandleCommandAsync(player, "/reset confirm " + token).ConfigureAwait(false);

        Assert.That(player.Money, Is.EqualTo(900));
        Assert.That(player.Level, Is.EqualTo(10));
        Assert.That(player.Attributes![Stats.Resets], Is.EqualTo(1));
        Assert.That(player.OpenedNpc, Is.Null);
    }

    /// <summary>
    /// Rejects requests that no longer belong to the viewed confirmation.
    /// </summary>
    [TestCase("wrong-token")]
    [TestCase("closed")]
    [TestCase("reopened")]
    [TestCase("changed-cost")]
    [TestCase("changed-balance")]
    [TestCase("query-invalidates")]
    [TestCase("disabled")]
    [TestCase("no-prepare")]
    [TestCase("unknown-command")]
    public async Task InvalidConfirmationDoesNotMutateCharacterAsync(string reason)
    {
        var player = await CreatePlayerAsync().ConfigureAwait(false);
        var command = new ResetChatCommandPlugIn();
        await command.HandleCommandAsync(player, "/reset prepare").ConfigureAwait(false);
        var token = GetToken(player);
        if (reason == "wrong-token")
        {
            token = Guid.NewGuid().ToString("N");
        }
        else if (reason is "closed" or "reopened")
        {
            var npc = player.OpenedNpc!;
            await new CloseNpcDialogAction().CloseNpcDialogAsync(player).ConfigureAwait(false);
            if (reason == "reopened")
            {
                await new TalkNpcAction().TalkToNpcAsync(player, npc).ConfigureAwait(false);
            }
        }
        else if (reason == "changed-cost")
        {
            player.GameContext.FeaturePlugIns.GetPlugIn<ResetFeaturePlugIn>()!.Configuration!.RequiredMoney = 200;
        }
        else if (reason == "changed-balance")
        {
            player.Money = 1100;
        }
        else if (reason == "query-invalidates")
        {
            await new ResetInfoChatCommandPlugIn().HandleCommandAsync(player, "/resetinfo").ConfigureAwait(false);
        }
        else if (reason == "disabled")
        {
            player.GameContext.FeaturePlugIns.GetPlugIn<ResetFeaturePlugIn>()!.Configuration = null;
        }
        else if (reason == "no-prepare")
        {
            await ResetNpcDialogAction.OpenAsync(player, player.OpenedNpc!).ConfigureAwait(false);
        }

        var money = player.Money;
        await command.HandleCommandAsync(player, reason == "unknown-command" ? "/reset invalid" : "/reset confirm " + token).ConfigureAwait(false);
        Assert.That(player.Money, Is.EqualTo(money));
        Assert.That(player.Level, Is.EqualTo(400));
        Assert.That(player.Attributes![Stats.Resets], Is.Zero);
    }

    /// <summary>
    /// Routes real public chat commands while an NPC session is open.
    /// </summary>
    [TestCase(372)]
    [TestCase(400)]
    public async Task PublicChatDispatchesQueryAndPreparationAsync(int level)
    {
        var player = await CreatePlayerAsync().ConfigureAwait(false);
        player.Attributes![Stats.Level] = level;
        player.GameContext.PlugInManager.RegisterPlugInAtPlugInPoint<IChatCommandPlugIn>(new ResetInfoChatCommandPlugIn());
        player.GameContext.PlugInManager.RegisterPlugInAtPlugInPoint<IChatCommandPlugIn>(new ResetChatCommandPlugIn());
        var view = Mock.Get(player.ViewPlugIns.GetPlugIn<IShowResetDialogPlugIn>()!);
        view.Invocations.Clear();
        var chat = new ChatMessageAction();
        await chat.ChatMessageAsync(player, player.Name, "/resetinfo", false).ConfigureAwait(false);
        view.Verify(v => v.ShowResetDialogAsync("", It.IsAny<string>(),
            It.Is<string>(text => text.StartsWith("已刷新当前转生条件", StringComparison.Ordinal) && text.Contains(level.ToString(CultureInfo.InvariantCulture))),
            "查询转生条件", "申请转生", "离开"), Times.Once);
        await chat.ChatMessageAsync(player, player.Name, "/reset prepare", false).ConfigureAwait(false);
        if (level < 400)
        {
            Assert.That(GetToken(player), Is.Empty);
            view.Verify(v => v.ShowResetDialogAsync("", It.IsAny<string>(),
                It.Is<string>(text => text.StartsWith("转生需要达到400级。", StringComparison.Ordinal)),
                "查询转生条件", "申请转生", "离开"), Times.Once);
            await chat.ChatMessageAsync(player, player.Name, "/reset confirm " + Guid.NewGuid().ToString("N"), false).ConfigureAwait(false);
        }
        else
        {
            Assert.That(GetToken(player), Has.Length.EqualTo(32));
            view.Verify(v => v.ShowResetDialogAsync(It.Is<string>(token => token.Length == 32), It.IsAny<string>(),
                It.Is<string>(text => text.StartsWith("请核对以下条件", StringComparison.Ordinal)),
                "查询转生条件", "确认转生", "离开"), Times.Once);
        }
        Assert.That(player.Level, Is.EqualTo(level));
        Assert.That(player.Money, Is.EqualTo(1000));
    }

    /// <summary>
    /// Preparation uses the configured level and invalidates an earlier quote when rejected.
    /// </summary>
    [TestCase(350)]
    [TestCase(450)]
    public async Task InsufficientLevelRejectsPreparationAndInvalidatesQuoteAsync(int requiredLevel)
    {
        var player = await CreatePlayerAsync().ConfigureAwait(false);
        var configuration = player.GameContext.FeaturePlugIns.GetPlugIn<ResetFeaturePlugIn>()!.Configuration!;
        configuration.RequiredLevel = requiredLevel;
        player.Attributes![Stats.Level] = requiredLevel;
        await ResetNpcDialogAction.HandleAsync(player, "prepare").ConfigureAwait(false);
        var token = GetToken(player);
        Assert.That(token, Has.Length.EqualTo(32));
        player.Attributes[Stats.Level] = requiredLevel - 1;
        await ResetNpcDialogAction.HandleAsync(player, "prepare").ConfigureAwait(false);
        Assert.That(GetToken(player), Is.Empty);
        await ResetNpcDialogAction.HandleAsync(player, "confirm", token).ConfigureAwait(false);
        Assert.That(player.Level, Is.EqualTo(requiredLevel - 1));
        Assert.That(player.Money, Is.EqualTo(1000));
        Assert.That(player.Attributes[Stats.Resets], Is.Zero);
        Assert.That(player.OpenedNpc, Is.Not.Null);
    }

    private static string GetToken(Player player)
    {
        var view = Mock.Get(player.ViewPlugIns.GetPlugIn<IShowResetDialogPlugIn>()!);
        return (string)view.Invocations.Last(i => i.Method.Name == nameof(IShowResetDialogPlugIn.ShowResetDialogAsync)).Arguments[0];
    }

    private static async ValueTask<Player> CreatePlayerAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        player.Culture = CultureInfo.GetCultureInfo("zh-CN");
        player.Attributes![Stats.Level] = 400;
        player.Money = 1000;
        player.GameContext.FeaturePlugIns.AddPlugIn(new ResetFeaturePlugIn
        {
            Configuration = new ResetConfiguration { RequiredMoney = 100, MoveHome = false, LogOut = false },
        }, true);
        var definition = player.PersistenceContext.CreateNew<MonsterDefinition>();
        definition.Number = 371;
        definition.Designation = "转生导师·里奥";
        definition.ObjectKind = NpcObjectKind.PassiveNpc;
        var plugIn = new ResetCharacterNpcPlugin { Configuration = new NpcTalkPlugInConfiguration { Npc = definition } };
        player.GameContext.PlugInManager.RegisterPlugInAtPlugInPoint<IPlayerTalkToNpcPlugIn>(plugIn);
        var npc = new NonPlayerCharacter(new MonsterSpawnArea
        {
            X1 = player.Position.X, X2 = player.Position.X, Y1 = player.Position.Y, Y2 = player.Position.Y,
        }, definition, player.CurrentMap!);
        await new TalkNpcAction().TalkToNpcAsync(player, npc).ConfigureAwait(false);
        return player;
    }
}
