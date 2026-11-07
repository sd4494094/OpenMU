// <copyright file="ResetStatsNpcTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameLogic.Resets;
using MUnique.OpenMU.GameServer.MessageHandler;

/// <summary>
/// Tests native NPC stat reset confirmations and their authorization and cost guards.
/// </summary>
[TestFixture]
public class ResetStatsNpcTest
{
    /// <summary>
    /// Confirms that a reset refunds points once without changing level or reset count.
    /// </summary>
    [Test]
    public async Task ConfirmationRefundsPointsAndChargesOnlyOnceAsync()
    {
        var player = await CreatePlayerAsync().ConfigureAwait(false);
        await OpenNpcAsync(player).ConfigureAwait(false);
        var handler = new ResetCharacterPointRequestHandlerPlugIn();

        await handler.HandlePacketAsync(player, new byte[] { 0xC1, 4, 0xF2, 0 }).ConfigureAwait(false);
        await handler.HandlePacketAsync(player, new byte[] { 0xC1, 4, 0xF2, 0 }).ConfigureAwait(false);

        Assert.That(player.Money, Is.EqualTo(900));
        Assert.That(player.SelectedCharacter!.LevelUpPoints, Is.EqualTo(107));
        Assert.That(player.Attributes![Stats.BaseStrength], Is.EqualTo(28));
        Assert.That(player.Level, Is.EqualTo(200));
        Assert.That(player.Attributes[Stats.Resets], Is.EqualTo(3));
        Assert.That(player.OpenedNpc, Is.Null);
    }

    /// <summary>
    /// Rejects confirmations outside an authorized, configured dialog.
    /// </summary>
    [TestCase("no-dialog")]
    [TestCase("wrong-window")]
    [TestCase("equipped")]
    [TestCase("disabled")]
    [TestCase("insufficient-money")]
    [TestCase("unsafe")]
    [TestCase("wrong-subcode")]
    [TestCase("short-packet")]
    public async Task RejectedConfirmationPreservesMoneyAndAttributesAsync(string reason)
    {
        var player = await CreatePlayerAsync(reason != "disabled").ConfigureAwait(false);
        if (reason != "no-dialog")
        {
            await OpenNpcAsync(player, reason == "wrong-window" ? NpcWindow.Lahap : NpcWindow.StatReInitializer).ConfigureAwait(false);
        }

        if (reason == "equipped")
        {
            var item = player.PersistenceContext.CreateNew<Item>();
            item.ItemSlot = 0;
            item.Definition = new ItemDefinition { Group = 0, Number = 0, Width = 1, Height = 1 };
            Assert.That(await player.Inventory!.AddItemAsync(0, item).ConfigureAwait(false), Is.True);
        }

        if (reason == "insufficient-money")
        {
            player.Money = 99;
        }

        if (reason == "unsafe")
        {
            player.CurrentMap!.Terrain.SafezoneMap[player.Position.X, player.Position.Y] = false;
        }

        var money = player.Money;
        var packet = reason switch
        {
            "wrong-subcode" => new byte[] { 0xC1, 4, 0xF2, 1 },
            "short-packet" => new byte[] { 0xC1, 3, 0xF2 },
            _ => new byte[] { 0xC1, 4, 0xF2, 0 },
        };
        await new ResetCharacterPointRequestHandlerPlugIn().HandlePacketAsync(player, packet).ConfigureAwait(false);

        Assert.That(player.Money, Is.EqualTo(money));
        Assert.That(player.Attributes![Stats.BaseStrength], Is.EqualTo(128));
        Assert.That(player.SelectedCharacter!.LevelUpPoints, Is.EqualTo(7));
        Assert.That(player.Level, Is.EqualTo(200));
        Assert.That(player.Attributes[Stats.Resets], Is.EqualTo(3));
    }

    private static async ValueTask<Player> CreatePlayerAsync(bool featureEnabled = true)
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        player.Money = 1000;
        player.Attributes![Stats.BaseStrength] = 128;
        player.Attributes[Stats.Level] = 200;
        player.Attributes[Stats.Resets] = 3;
        player.SelectedCharacter!.LevelUpPoints = 7;
        player.CurrentMap!.Terrain.SafezoneMap[player.Position.X, player.Position.Y] = true;
        if (featureEnabled)
        {
            player.GameContext.FeaturePlugIns.AddPlugIn(new StatResetFeaturePlugIn
            {
                Configuration = new StatResetConfiguration { RequiredMoney = 100, MoveHome = false, LogOut = false },
            }, true);
        }

        return player;
    }

    private static async ValueTask OpenNpcAsync(Player player, NpcWindow window = NpcWindow.StatReInitializer)
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
        definition.ObjectKind = NpcObjectKind.PassiveNpc;
        definition.NpcWindow = window;
        var npc = new NonPlayerCharacter(spawn, definition, player.CurrentMap!);
        await new TalkNpcAction().TalkToNpcAsync(player, npc).ConfigureAwait(false);
    }
}
