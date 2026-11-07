// <copyright file="LevelingWarpAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.LevelingWarp;

using System.ComponentModel;
using System.Runtime.CompilerServices;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.Views.NPC;
using MUnique.OpenMU.Interfaces;
using Nito.AsyncEx;

/// <summary>
/// Authorizes one selection, validates the landing before charging, and closes the NPC before switching maps.
/// </summary>
public static class LevelingWarpAction
{
    private static readonly ConditionalWeakTable<Player, Session> Sessions = new();

    /// <summary>Records the current client's explicit menu capability.</summary>
    /// <param name="player">The current connection.</param>
    public static void RegisterClient(Player player) => Sessions.GetValue(player, _ => new Session()).Supported = true;

    /// <summary>Opens a read-only menu; displaying it never charges or moves a player.</summary>
    /// <param name="player">The player.</param>
    /// <param name="npc">The configured NPC instance.</param>
    /// <returns>Whether a supported menu was opened.</returns>
    public static async ValueTask<bool> OpenAsync(Player player, NonPlayerCharacter npc)
    {
        var configuration = GetConfiguration(player);
        if (!Sessions.TryGetValue(player, out var capability) || !capability.Supported
            || configuration is null || !CanInteract(player, npc, configuration)
            || player.ViewPlugIns.GetPlugIn<IShowLevelingWarpDialogPlugIn>() is null)
        {
            await player.ShowLocalizedBlueMessageAsync(nameof(PlayerMessage.LevelingWarpUnavailable)).ConfigureAwait(false);
            return false;
        }

        var session = Sessions.GetValue(player, _ => new Session());
        using var guard = await session.Lock.LockAsync().ConfigureAwait(false);
        if (!ReferenceEquals(GetConfiguration(player), configuration) || !CanInteract(player, npc, configuration))
        {
            return false;
        }

        session.Token = Guid.NewGuid().ToString("N");
        session.Npc = npc;
        session.Character = player.SelectedCharacter;
        session.Configuration = configuration;
        session.Destinations = configuration.Destinations.Where(d => d.IsEnabled).ToDictionary(d => d.Number, Snapshot.Create);
        var quotes = session.Destinations.Values.Select(d => Quote(player, d)).ToArray();
        await player.InvokeViewPlugInAsync<IShowLevelingWarpDialogPlugIn>(view => view.ShowLevelingWarpDialogAsync(
            session.Token,
            player.GetLocalizedMessage(nameof(PlayerMessage.LevelingWarpTitle)),
            player.GetLocalizedMessage(nameof(PlayerMessage.LevelingWarpClose)),
            player.GetLocalizedMessage(nameof(PlayerMessage.LevelingWarpLevel)),
            player.GetLocalizedMessage(nameof(PlayerMessage.LevelingWarpZen)),
            quotes)).ConfigureAwait(false);
        return true;
    }

    /// <summary>Executes a single authorized selection, rejecting stale sessions and changed quotes.</summary>
    /// <param name="player">The player.</param>
    /// <param name="token">The one-use menu token.</param>
    /// <param name="number">The stable destination number.</param>
    /// <returns>The task.</returns>
    public static async ValueTask SelectAsync(Player player, string token, int number)
    {
        if (!Sessions.TryGetValue(player, out var session))
        {
            return;
        }

        // Serialize token consumption, charging and closing. Never re-enter OpenAsync while holding this lock.
        using var guard = await session.Lock.LockAsync().ConfigureAwait(false);
        if (session.Token != token || string.IsNullOrEmpty(token))
        {
            return;
        }

        session.Token = string.Empty;
        var configuration = GetConfiguration(player);
        if (configuration is null || !ReferenceEquals(configuration, session.Configuration)
            || !ReferenceEquals(player.SelectedCharacter, session.Character)
            || session.Npc is not { } npc || !CanInteract(player, npc, configuration)
            || !session.Destinations.TryGetValue(number, out var quoted)
            || configuration.Destinations.SingleOrDefault(d => d.Number == number && d.IsEnabled) is not { } destination
            || !quoted.Equals(Snapshot.Create(destination)))
        {
            await RejectAsync(player, nameof(PlayerMessage.LevelingWarpChanged)).ConfigureAwait(false);
            return;
        }

        await ExecuteAsync(player, session, destination, quoted).ConfigureAwait(false);
    }

    private static LevelingWarpConfiguration? GetConfiguration(Player player)
    {
        var config = player.GameContext.FeaturePlugIns.GetPlugIn<LevelingWarpFeaturePlugIn>()?.Configuration;
        return config is { MaximumDistance: > 0 and <= 10, Destinations.Count: > 0 and <= 64 }
            && config.Destinations.All(d => d.Number is > 0 and <= ushort.MaxValue && d.Costs >= 0 && d.LevelRequirement is >= 0 and <= ushort.MaxValue)
            && config.Destinations.Select(d => d.Number).Distinct().Count() == config.Destinations.Count
            ? config : null;
    }

    private static bool CanInteract(Player player, NonPlayerCharacter npc, LevelingWarpConfiguration config)
    {
        return player.Account is { IsBot: false } && player.IsAlive
            && player.SelectedCharacter is not null && player.CurrentMap is { } map
            && npc.CurrentMap == map && npc.Definition.IsActive
            && player.PlayerState.CurrentState == PlayerState.NpcDialogOpened
            && ReferenceEquals(player.OpenedNpc, npc)
            && player.Position.EuclideanDistanceSquaredTo(npc.Position) <= config.MaximumDistance * config.MaximumDistance
            && map.Terrain.HasLineOfSight(player.Position, npc.Position)
            && player.GameContext.PlugInManager.GetActivePlugInsOf<IPlayerTalkToNpcPlugIn>()
                .OfType<LevelingWarpNpcPlugIn>().Any(p => p.IsConfiguredNpc(npc));
    }

    private static LevelingWarpQuote Quote(Player player, Snapshot destination)
    {
        var level = player.SelectedCharacter!.GetEffectiveMoveLevelRequirement(destination.Level);
        var enabled = player.Level >= level && player.Money >= destination.Costs && destination.Map is { IsActive: true }
            && !destination.Map.TryGetRequirementError(player, out _);
        return new LevelingWarpQuote(
            destination.Number,
            destination.Name.GetTranslation(player.Culture) ?? string.Empty,
            destination.Description.GetTranslation(player.Culture) ?? string.Empty,
            level,
            destination.Costs,
            enabled);
    }

    private static async ValueTask ExecuteAsync(Player player, Session session, LevelingWarpDestination destination, Snapshot quote)
    {
        var gate = destination.Gate;
        var target = quote.Map is { IsActive: true } map
            ? await player.GameContext.GetMapAsync(map.Number.ToUnsigned()).ConfigureAwait(false) : null;
        if (gate is null || target is null || !ReferenceEquals(target.Definition, quote.Map)
            || quote.X != quote.X2 || quote.Y != quote.Y2 || !target.Terrain.WalkMap[quote.X, quote.Y]
            || !Quote(player, quote).Enabled)
        {
            await RejectAsync(player, nameof(PlayerMessage.LevelingWarpRequirements)).ConfigureAwait(false);
            return;
        }

        if (player.GameContext.PlugInManager.GetPlugInPoint<IWarpGateEnteringPlugIn>() is { } entering)
        {
            var args = new CancelEventArgs();
            await entering.WarpGateEnteringAsync(player, gate, args).ConfigureAwait(false);
            if (args.Cancel)
            {
                await new CloseNpcDialogAction().CloseNpcDialogAsync(player).ConfigureAwait(false);
                return;
            }
        }

        // Revalidate after extensibility callbacks, then close while the NPC state is still valid.
        if (!ReferenceEquals(GetConfiguration(player), session.Configuration)
            || !ReferenceEquals(player.SelectedCharacter, session.Character)
            || session.Npc is not { } npc || !CanInteract(player, npc, session.Configuration!)
            || !session.Configuration!.Destinations.Contains(destination) || !destination.IsEnabled
            || !quote.Equals(Snapshot.Create(destination)) || !Quote(player, quote).Enabled
            || !target.Terrain.WalkMap[quote.X, quote.Y])
        {
            await RejectAsync(player, nameof(PlayerMessage.LevelingWarpChanged)).ConfigureAwait(false);
            return;
        }

        var character = player.SelectedCharacter!;
        await new CloseNpcDialogAction().CloseNpcDialogAsync(player).ConfigureAwait(false);
        if (!ReferenceEquals(player.SelectedCharacter, character)
            || !ReferenceEquals(GetConfiguration(player), session.Configuration)
            || !destination.IsEnabled || !quote.Equals(Snapshot.Create(destination))
            || !Quote(player, quote).Enabled || !target.Terrain.WalkMap[quote.X, quote.Y]
            || player.PlayerState.CurrentState != PlayerState.EnteredWorld || player.OpenedNpc is not null
            || !player.TryRemoveMoney(quote.Costs))
        {
            return;
        }

        try
        {
            await player.WarpToAsync(gate).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RefundIfNotPlaced(player, character, quote);
            player.Logger.LogError(exception, "Leveling warp {Number} failed for {Player}.", quote.Number, player);
            throw;
        }

        RefundIfNotPlaced(player, character, quote);
        player.Logger.LogInformation(
            "Leveling warp dispatch: {Player}, destination {Number}, map {Map}, X {X}, Y {Y}, cost {Costs}, placed {Placed}.",
            player,
            quote.Number,
            quote.Map?.Number,
            quote.X,
            quote.Y,
            quote.Costs,
            IsPlaced(character, quote));
    }

    private static bool IsPlaced(Character character, Snapshot quote) =>
        ReferenceEquals(character.CurrentMap, quote.Map)
        && character.PositionX == quote.X && character.PositionY == quote.Y;

    private static void RefundIfNotPlaced(Player player, Character character, Snapshot quote)
    {
        // A lost client acknowledgement is not proof of failure: never refund an already placed character.
        if (!IsPlaced(character, quote))
        {
            if (ReferenceEquals(player.SelectedCharacter, character))
            {
                player.Money += quote.Costs;
            }
            else
            {
                character.Inventory!.Money += quote.Costs;
            }

            player.Logger.LogWarning("Refunded unplaced leveling warp {Number} for {Player}.", quote.Number, player);
        }
    }

    private static async ValueTask RejectAsync(Player player, string resource)
    {
        await new CloseNpcDialogAction().CloseNpcDialogAsync(player).ConfigureAwait(false);
        await player.ShowLocalizedBlueMessageAsync(resource).ConfigureAwait(false);
    }

    private sealed class Session
    {
        public AsyncLock Lock { get; } = new();

        public bool Supported { get; set; }

        public string Token { get; set; } = string.Empty;

        public NonPlayerCharacter? Npc { get; set; }

        public Character? Character { get; set; }

        public LevelingWarpConfiguration? Configuration { get; set; }

        public Dictionary<int, Snapshot> Destinations { get; set; } = [];
    }

    private sealed record Snapshot(int Number, LocalizedString Name, LocalizedString Description, int Level, int Costs, ExitGate? Gate, GameMapDefinition? Map, byte X, byte Y, byte X2, byte Y2)
    {
        public static Snapshot Create(LevelingWarpDestination destination) =>
            new(destination.Number, destination.Name, destination.Description, destination.LevelRequirement, destination.Costs, destination.Gate, destination.Gate?.Map, destination.Gate?.X1 ?? 0, destination.Gate?.Y1 ?? 0, destination.Gate?.X2 ?? 0, destination.Gate?.Y2 ?? 0);
    }
}
