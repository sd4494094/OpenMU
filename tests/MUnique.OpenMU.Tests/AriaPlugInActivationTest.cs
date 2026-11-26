// <copyright file="AriaPlugInActivationTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.ComponentModel.Design;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Aria;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.PlugIns.ChatCommands;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.GameLogic.Views.NPC;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.PlugIns;

/// <summary>Exercises optional Aria integration through the real plugin manager's service container.</summary>
[TestFixture]
public class AriaPlugInActivationTest
{
    /// <summary>All aliases load in a standalone host and inject a service when one is available.</summary>
    /// <param name="enabled">Whether the optional service is registered.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async Task ChatAliasesLoadWithOptionalServiceAsync(bool enabled)
    {
        var service = new Mock<IAriaChatService>();
        using var services = new ServiceContainer();
        if (enabled)
        {
            services.AddService(typeof(IAriaChatService), service.Object);
        }

        var manager = new PlugInManager(null, NullLoggerFactory.Instance, services, null);
        manager.RegisterPlugIn<IChatCommandPlugIn, AriaChatCommandPlugIn>();
        manager.RegisterPlugIn<IChatCommandPlugIn, AriaLowerChatCommandPlugIn>();
        manager.RegisterPlugIn<IChatCommandPlugIn, AriaChineseChatCommandPlugIn>();
        manager.RegisterPlugIn<IChatCommandPlugIn, AriaAssistantChatCommandPlugIn>();
        var plugins = manager.GetActivePlugInsOf<IChatCommandPlugIn>().ToList();
        Assert.That(plugins, Has.Count.EqualTo(4));
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        foreach (var plugin in plugins)
        {
            await plugin.HandleCommandAsync(player, plugin.Key + " 你好").ConfigureAwait(false);
        }

        service.Verify(value => value.TrySubmit(player, "你好"), enabled ? Times.Exactly(4) : Times.Never());
        if (!enabled)
        {
            var expected = player.GetLocalizedMessage("AriaUnavailable");
            Mock.Get(player.ViewPlugIns.GetPlugIn<IShowMessagePlugIn>()!).Verify(
                view => view.ShowMessageAsync(expected, MessageType.BlueNormal), Times.Exactly(4));
        }
    }

    /// <summary>The decorative NPC remains usable with AI disabled and advertises AI only when injected.</summary>
    /// <param name="enabled">Whether the optional service is registered.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async Task CreatorGoddessLoadsWithOptionalServiceAsync(bool enabled)
    {
        using var services = new ServiceContainer();
        if (enabled)
        {
            services.AddService(typeof(IAriaChatService), Mock.Of<IAriaChatService>());
        }

        var manager = new PlugInManager(null, NullLoggerFactory.Instance, services, null);
        manager.RegisterPlugIn<IPlayerTalkToNpcPlugIn, CreatorGoddessNpcPlugIn>();
        var plugin = manager.GetActivePlugInsOf<IPlayerTalkToNpcPlugIn>().OfType<CreatorGoddessNpcPlugIn>().Single();
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var definition = player.PersistenceContext.CreateNew<MonsterDefinition>();
        definition.Number = CreatorGoddessNpcPlugIn.NpcNumber;
        var npc = new NonPlayerCharacter(new MonsterSpawnArea(), definition, player.CurrentMap!);
        plugin.Configuration = new NpcTalkPlugInConfiguration { Npc = definition };
        var args = new NpcTalkEventArgs();
        await plugin.PlayerTalksToNpcAsync(player, npc, args).ConfigureAwait(false);
        var expected = player.GetLocalizedMessage(enabled ? "CreatorGoddessAiGreeting" : "CreatorGoddessGreeting");
        Mock.Get(player.ViewPlugIns.GetPlugIn<IShowMessageOfObjectPlugIn>()!).Verify(
            view => view.ShowMessageOfObjectAsync(expected, npc), Times.Once());
        Assert.That(args.HasBeenHandled, Is.True);
        Assert.That(args.LeavesDialogOpen, Is.False);
    }
}
