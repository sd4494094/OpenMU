// <copyright file="StatAdvisorAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Resets;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.Views.Character;
using MUnique.OpenMU.GameLogic.Views.NPC;
using MUnique.OpenMU.Interfaces;

/// <summary>Explicit previews and one-use execution for the stat advisor.</summary>
public static class StatAdvisorAction
{
    private const int ConfirmationLifetimeSeconds = 120;
    private static readonly ConditionalWeakTable<Player, Session> Sessions = new();

    /// <summary>Invalidates confirmations when the NPC conversation ends.</summary>
    /// <param name="player">The player.</param>
    public static void Close(Player player)
    {
        if (Sessions.TryGetValue(player, out var session))
        {
            session.Active = false;
            session.Token = string.Empty;
        }
    }

    /// <summary>Whether the current conversation opted into the extended workflow.</summary>
    /// <param name="player">The player.</param>
    /// <returns>Whether a legacy direct confirmation must be rejected.</returns>
    public static bool IsActive(Player player) => Sessions.TryGetValue(player, out var session) && session.Active;

    /// <summary>Handles open, navigation, preview and explicit confirmation under the player persistence lock.</summary>
    /// <param name="player">The player.</param>
    /// <param name="operation">Open=0, reset preview=1, reset confirm=2, allocation=3, allocation preview=4, allocation confirm=5, home=6.</param>
    /// <param name="sessionId">Conversation identity, empty only when opening.</param>
    /// <param name="token">One-use preview identity.</param>
    /// <param name="amounts">The five stable attribute increases.</param>
    public static ValueTask HandleAsync(Player player, byte operation, string sessionId, string token, int[] amounts)
        => player.RunPersistenceExclusiveAsync(() => HandleCoreAsync(player, operation, sessionId, token, amounts));

    private static async ValueTask HandleCoreAsync(Player player, byte operation, string sessionId, string token, int[] amounts)
    {
        if (operation > 6 || amounts.Length != StatAdvisorRules.Attributes.Length
            || player.ViewPlugIns.GetPlugIn<IShowStatAdvisorPlugIn>() is null || !IsValidNpc(player))
        {
            return;
        }

        var session = Sessions.GetValue(player, _ => new Session());
        if (operation == 0)
        {
            session.Active = true;
            session.Id = Guid.NewGuid().ToString("N");
            session.Character = player.SelectedCharacter;
            session.Npc = player.OpenedNpc;
            session.CompletedToken = string.Empty;
            session.ClearInputs = true;
        }
        else if (!session.Active || session.Id != sessionId
            || !ReferenceEquals(session.Character, player.SelectedCharacter) || !ReferenceEquals(session.Npc, player.OpenedNpc))
        {
            return;
        }

        if (operation is 2 or 5)
        {
            await ConfirmAsync(player, session, operation == 2, token).ConfigureAwait(false);
            return;
        }

        session.Token = string.Empty;
        session.Reset = operation == 1;
        session.Amounts = operation == 4 ? amounts.ToArray() : new int[StatAdvisorRules.Attributes.Length];
        session.ClearInputs = false;
        var page = operation == 1 ? 1 : operation is 3 or 4 ? 2 : 0;
        var failure = operation == 1 ? StatAdvisorRules.ResetFailure(player)
            : operation == 4 ? StatAdvisorRules.AllocationFailure(player, amounts) : string.Empty;
        if (operation is 1 or 4 && failure.Length == 0)
        {
            session.Token = Guid.NewGuid().ToString("N");
            session.ExpiresAt = DateTime.UtcNow.AddSeconds(ConfirmationLifetimeSeconds);
            session.Fingerprint = StatAdvisorRules.Fingerprint(player, session.Reset);
            page = session.Reset ? 1 : 3;
        }

        await ShowAsync(player, session, page, failure).ConfigureAwait(false);
    }

    private static bool IsValidNpc(Player player) => player.PlayerState.CurrentState == PlayerState.NpcDialogOpened
        && player.OpenedNpc is { Definition.NpcWindow: NpcWindow.StatReInitializer } npc
        && npc.CurrentMap == player.CurrentMap && player.SelectedCharacter is not null && player.Attributes is not null;

    private static async ValueTask ConfirmAsync(Player player, Session session, bool reset, string token)
    {
        if (!reset && token.Length > 0 && token == session.CompletedToken)
        {
            session.ClearInputs = true;
            await ShowAsync(player, session, 2, player.GetLocalizedMessage("AdvisorAlreadyApplied")).ConfigureAwait(false);
            return;
        }

        if (token.Length == 0 || token != session.Token || reset != session.Reset)
        {
            session.Token = string.Empty;
            session.ClearInputs = false;
            await ShowAsync(player, session, reset ? 1 : 2, player.GetLocalizedMessage("AdvisorExpired")).ConfigureAwait(false);
            return;
        }

        session.Token = string.Empty;
        session.ClearInputs = false;
        var failure = reset ? StatAdvisorRules.ResetFailure(player) : StatAdvisorRules.AllocationFailure(player, session.Amounts);
        if (DateTime.UtcNow > session.ExpiresAt || session.Fingerprint != StatAdvisorRules.Fingerprint(player, reset))
        {
            failure = player.GetLocalizedMessage("AdvisorStateChanged");
        }

        if (failure.Length > 0)
        {
            await ShowAsync(player, session, reset ? 1 : 2, failure).ConfigureAwait(false);
            return;
        }

        if (reset)
        {
            // Mark the extension inactive before delegating; the existing action releases the NPC
            // and rechecks its normal rules. Never send the legacy request from the new client.
            session.Active = false;
            await ShowAsync(player, session, 4, string.Empty).ConfigureAwait(false);
            await new ResetStatsAction(player).ResetStatsFromNpcAsync().ConfigureAwait(false);
            return;
        }

        ApplyAllocation(player, session.Amounts);
        session.CompletedToken = token;
        session.Amounts = new int[StatAdvisorRules.Attributes.Length];
        session.ClearInputs = true;

        // Packet handlers and ordinary stat/chat requests already share this player's reentrant
        // persistence lock. Validation and every write above run without yielding, and saving
        // observes the complete allocation rather than a partially modified character.
        var saved = false;
        try
        {
            saved = await player.SaveProgressAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            player.Logger.LogError(exception, "Could not save the batch attribute allocation for player {Player}.", player);
        }

        await player.InvokeViewPlugInAsync<IUpdateCharacterStatsPlugIn>(v => v.UpdateCharacterStatsAsync()).ConfigureAwait(false);
        await player.InvokeViewPlugInAsync<IUpdateCharacterBaseStatsPlugIn>(v => v.UpdateCharacterBaseStatsAsync()).ConfigureAwait(false);
        await ShowAsync(player, session, 2, player.GetLocalizedMessage(saved ? "AdvisorApplied" : "AdvisorAppliedSavePending")).ConfigureAwait(false);
    }

    private static void ApplyAllocation(Player player, int[] amounts)
    {
        var before = StatAdvisorRules.GetStats(player);
        var points = player.SelectedCharacter!.LevelUpPoints;
        try
        {
            foreach (var row in before)
            {
                player.Attributes![StatAdvisorRules.Attributes[row.Id]] = row.Current + amounts[row.Id];
            }

            player.SelectedCharacter.LevelUpPoints = points - amounts.Sum();
        }
        catch
        {
            foreach (var row in before)
            {
                player.Attributes![StatAdvisorRules.Attributes[row.Id]] = row.Current;
            }

            player.SelectedCharacter.LevelUpPoints = points;
            throw;
        }
    }

    private static ValueTask ShowAsync(Player player, Session session, int page, string feedback)
    {
        var body = page == 1 ? StatAdvisorRules.ResetDescription(player)
            : player.GetLocalizedMessage(page == 0 ? "AdvisorHomeNotice" : "AdvisorAllocationNotice");
        var message = feedback.Length == 0 ? body : feedback + "\n" + body;
        string[] names =
        [
            "Reset", "Allocate", "Leave", "Back", "Clear", "Next", "ConfirmReset", "ConfirmAllocation",
            "Attribute", "Current", "Increase", "Result", "Available", "Total", "Remaining", "InvalidInput", "Waiting",
        ];
        var labels = names.ToDictionary(n => n, n => player.GetLocalizedMessage("Advisor" + n));
        var dialog = new StatAdvisorDialog(
            session.Id,
            session.Token,
            page,
            session.Npc!.Definition.Designation.GetTranslation(player.Culture) ?? string.Empty,
            message,
            session.Token.Length > 0,
            player.SelectedCharacter!.LevelUpPoints,
            StatAdvisorRules.GetStats(player, session.Amounts),
            labels,
            session.ClearInputs);
        return player.InvokeViewPlugInAsync<IShowStatAdvisorPlugIn>(v => v.ShowStatAdvisorAsync(dialog));
    }

    private sealed class Session
    {
        public bool Active { get; set; }

        public string Id { get; set; } = string.Empty;

        public Character? Character { get; set; }

        public NonPlayerCharacter? Npc { get; set; }

        public string Token { get; set; } = string.Empty;

        public string CompletedToken { get; set; } = string.Empty;

        public bool Reset { get; set; }

        public int[] Amounts { get; set; } = new int[StatAdvisorRules.Attributes.Length];

        public bool ClearInputs { get; set; }

        public string Fingerprint { get; set; } = string.Empty;

        public DateTime ExpiresAt { get; set; }
    }
}
