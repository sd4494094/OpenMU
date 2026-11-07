// <copyright file="LevelingWarpTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.LevelingWarp;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.Views.NPC;

/// <summary>
/// Exercises NPC authorization, display-only text, quote integrity and one-time execution.
/// </summary>
[TestFixture]
public class LevelingWarpTest
{
    /// <summary>Opening only displays full names and never moves or charges.</summary>
    [Test]
    public async Task OpenIsReadOnlyAsync()
    {
        var player = await CreateAsync().ConfigureAwait(false);
        var entries = GetEntries(player);
        Assert.That(entries.Single().Name, Is.EqualTo("地下城二层"));
        Assert.That(entries.Single().Description, Is.EqualTo("幽灵房"));
        Assert.That(player.Money, Is.EqualTo(1000));
        Assert.That(player.OpenedNpc, Is.Not.Null);
    }

    /// <summary>The stable identifier remains authoritative after reordering and renaming.</summary>
    [Test]
    public async Task SelectionChargesAndMovesExactlyOnceAsync()
    {
        var player = await CreateAsync().ConfigureAwait(false);
        var token = GetToken(player);
        await LevelingWarpAction.SelectAsync(player, token, 31).ConfigureAwait(false);
        await LevelingWarpAction.SelectAsync(player, token, 31).ConfigureAwait(false);
        Assert.That(player.Money, Is.EqualTo(900));
        Assert.That(player.SelectedCharacter!.PositionX, Is.EqualTo(15));
        Assert.That(player.SelectedCharacter.PositionY, Is.EqualTo(15));
        Assert.That(player.OpenedNpc, Is.Null);
    }

    /// <summary>Rejects stale, forged and no-longer-eligible requests without charging.</summary>
    [TestCase("wrong-token")]
    [TestCase("unknown-number")]
    [TestCase("closed")]
    [TestCase("reopened")]
    [TestCase("bot")]
    [TestCase("distance")]
    [TestCase("disabled")]
    [TestCase("changed-price")]
    [TestCase("changed-position")]
    [TestCase("changed-name")]
    [TestCase("changed-level")]
    [TestCase("insufficient-money")]
    [TestCase("insufficient-level")]
    [TestCase("dead")]
    [TestCase("blocked")]
    [TestCase("inactive-map")]
    [TestCase("destination-disabled")]
    public async Task InvalidSelectionCannotChargeOrMoveAsync(string reason)
    {
        var player = await CreateAsync().ConfigureAwait(false);
        var token = GetToken(player);
        var feature = player.GameContext.FeaturePlugIns.GetPlugIn<LevelingWarpFeaturePlugIn>()!;
        var destination = feature.Configuration!.Destinations.Single();
        var npc = player.OpenedNpc!;
        int number = 31;
        switch (reason)
        {
            case "wrong-token":
                token = Guid.NewGuid().ToString("N");
                break;
            case "unknown-number":
                number = 999;
                break;
            case "closed":
                await new CloseNpcDialogAction().CloseNpcDialogAsync(player).ConfigureAwait(false);
                break;
            case "reopened":
                await new CloseNpcDialogAction().CloseNpcDialogAsync(player).ConfigureAwait(false);
                await new TalkNpcAction().TalkToNpcAsync(player, npc).ConfigureAwait(false);
                break;
            case "bot":
                player.Account!.IsBot = true;
                break;
            case "distance":
                player.SelectedCharacter!.PositionX = 50;
                break;
            case "disabled":
                feature.Configuration = null;
                break;
            case "changed-price":
                destination.Costs = 200;
                break;
            case "changed-position":
                destination.Gate!.X1 = destination.Gate.X2 = 16;
                break;
            case "changed-name":
                destination.Name = "Changed";
                break;
            case "changed-level":
                destination.LevelRequirement = 401;
                break;
            case "dead":
                player.IsAlive = false;
                break;
            case "insufficient-level":
                player.Attributes![Stats.Level] = 1;
                break;
            case "insufficient-money":
                player.Money = 1;
                break;
            case "blocked":
                player.CurrentMap!.Terrain.ApplyTerrainAttribute(15, 15, TerrainAttributeType.Blocked, true);
                break;
            case "inactive-map":
                destination.Gate!.Map!.IsActive = false;
                break;
            case "destination-disabled":
                destination.IsEnabled = false;
                break;
        }

        var money = player.Money;
        var x = player.SelectedCharacter!.PositionX;
        var y = player.SelectedCharacter.PositionY;
        await LevelingWarpAction.SelectAsync(player, token, number).ConfigureAwait(false);
        Assert.That(player.Money, Is.EqualTo(money));
        Assert.That(player.SelectedCharacter.PositionX, Is.EqualTo(x));
        Assert.That(player.SelectedCharacter.PositionY, Is.EqualTo(y));
    }

    /// <summary>Plain cancellation releases the player for the next NPC.</summary>
    [Test]
    public async Task CancellationAllowsAnotherNpcAsync()
    {
        var player = await CreateAsync().ConfigureAwait(false);
        var npc = player.OpenedNpc!;
        await new CloseNpcDialogAction().CloseNpcDialogAsync(player).ConfigureAwait(false);
        Assert.That(player.OpenedNpc, Is.Null);
        Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.EnteredWorld));
        await new TalkNpcAction().TalkToNpcAsync(player, npc).ConfigureAwait(false);
        Assert.That(player.OpenedNpc, Is.SameAs(npc));
    }

    /// <summary>Server-side bot and unsupported client rejection does not retain a dialogue.</summary>
    [TestCase(true)]
    [TestCase(false)]
    public async Task BotOrUnsupportedClientCannotOpenAsync(bool bot)
    {
        var player = await CreateAsync(support: bot).ConfigureAwait(false);
        var npc = GetNpc(player);
        await new CloseNpcDialogAction().CloseNpcDialogAsync(player).ConfigureAwait(false);
        player.Account!.IsBot = bot;
        await new TalkNpcAction().TalkToNpcAsync(player, npc).ConfigureAwait(false);
        Assert.That(player.OpenedNpc, Is.Null);
        Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.EnteredWorld));
        Assert.That(player.Money, Is.EqualTo(1000));
    }

    /// <summary>Callbacks cannot bypass configuration or interaction checks.</summary>
    [TestCase("cancel")]
    [TestCase("feature")]
    [TestCase("moved")]
    [TestCase("price")]
    public async Task CallbackChangesCannotChargeAsync(string reason)
    {
        var player = await CreateAsync().ConfigureAwait(false);
        var token = GetToken(player);
        var feature = player.GameContext.FeaturePlugIns.GetPlugIn<LevelingWarpFeaturePlugIn>()!;
        var callback = new Mock<IWarpGateEnteringPlugIn>();
        callback.Setup(p => p.WarpGateEnteringAsync(player, It.IsAny<ExitGate>(), It.IsAny<CancelEventArgs>()))
            .Callback<Player, ExitGate, CancelEventArgs>((p, gate, args) =>
            {
                switch (reason)
                {
                    case "cancel":
                        args.Cancel = true;
                        break;
                    case "feature":
                        feature.Configuration = null;
                        break;
                    case "moved":
                        p.SelectedCharacter!.PositionX = 50;
                        break;
                    case "price":
                        feature.Configuration!.Destinations.Single().Costs = 200;
                        break;
                }
            }).Returns(ValueTask.CompletedTask);
        player.GameContext.PlugInManager.RegisterPlugInAtPlugInPoint<IWarpGateEnteringPlugIn>(callback.Object);
        await LevelingWarpAction.SelectAsync(player, token, 31).ConfigureAwait(false);
        Assert.That(player.Money, Is.EqualTo(1000));
        Assert.That(player.SelectedCharacter!.PositionY, Is.EqualTo(10));
        Assert.That(player.OpenedNpc, Is.Null);
    }

    private static NonPlayerCharacter GetNpc(Player player) => Npcs.TryGetValue(player, out var npc) ? npc : throw new InvalidOperationException();

    private static readonly ConditionalWeakTable<Player, NonPlayerCharacter> Npcs = new();

    private static string GetToken(Player player) => (string)GetInvocation(player).Arguments[0];

    private static IReadOnlyList<LevelingWarpQuote> GetEntries(Player player) =>
        (IReadOnlyList<LevelingWarpQuote>)GetInvocation(player).Arguments[5];

    private static Moq.IInvocation GetInvocation(Player player) =>
        Mock.Get(player.ViewPlugIns.GetPlugIn<IShowLevelingWarpDialogPlugIn>()!).Invocations.Last();

    private static async ValueTask<Player> CreateAsync(bool support = true)
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        player.Culture = CultureInfo.GetCultureInfo("zh-CN");
        player.Attributes![Stats.Level] = 400;
        player.Money = 1000;
        player.IsAlive = true;
        player.Account!.IsBot = false;
        player.SelectedCharacter!.PositionX = player.SelectedCharacter.PositionY = 10;
        player.CurrentMap!.Definition.IsActive = true;
        var gate = player.PersistenceContext.CreateNew<ExitGate>();
        gate.Map = player.CurrentMap.Definition;
        gate.X1 = gate.X2 = gate.Y1 = gate.Y2 = 15;
        var destination = new LevelingWarpDestination
        {
            Number = 31, Name = "地下城二层", Description = "幽灵房", Gate = gate, LevelRequirement = 100, Costs = 100,
        };
        player.GameContext.FeaturePlugIns.AddPlugIn(new LevelingWarpFeaturePlugIn
        {
            Configuration = new LevelingWarpConfiguration { Destinations = [destination] },
        }, true);
        var definition = player.PersistenceContext.CreateNew<MonsterDefinition>();
        definition.Number = 710;
        definition.IsActive = true;
        definition.Designation = "传送使者·诺亚";
        definition.ObjectKind = NpcObjectKind.PassiveNpc;
        var plugin = new LevelingWarpNpcPlugIn { Configuration = new NpcTalkPlugInConfiguration { Npc = definition } };
        player.GameContext.PlugInManager.RegisterPlugInAtPlugInPoint<IPlayerTalkToNpcPlugIn>(plugin);
        var npc = new NonPlayerCharacter(new MonsterSpawnArea { X1 = 10, X2 = 10, Y1 = 10, Y2 = 10 }, definition, player.CurrentMap);
        npc.Position = player.Position;
        Npcs.Add(player, npc);
        if (support)
        {
            LevelingWarpAction.RegisterClient(player);
        }

        await new TalkNpcAction().TalkToNpcAsync(player, npc).ConfigureAwait(false);
        return player;
    }
}
