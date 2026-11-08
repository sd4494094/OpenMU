// <copyright file="ResetNpcDialogAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Resets;

using System.Runtime.CompilerServices;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.Views.NPC;
using MUnique.OpenMU.Interfaces;
using Nito.AsyncEx;

/// <summary>
/// Presents reset requirements and authorizes a single, explicitly confirmed NPC reset.
/// </summary>
public static class ResetNpcDialogAction
{
    /// <summary>
    /// A confirmation expires after two minutes to avoid accepting an abandoned quote.
    /// </summary>
    internal const int ConfirmationLifetimeSeconds = 120;

    private static readonly ConditionalWeakTable<Player, Session> Sessions = new();

    /// <summary>
    /// Opens a menu without changing the character.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="npc">The configured reset NPC.</param>
    /// <returns>Whether this client supports the menu.</returns>
    public static async ValueTask<bool> OpenAsync(Player player, NonPlayerCharacter npc)
    {
        if (player.ViewPlugIns.GetPlugIn<IShowResetDialogPlugIn>() is null)
        {
            await new ResetInfoChatCommandPlugIn().HandleCommandAsync(player, "/resetinfo").ConfigureAwait(false);
            return false;
        }

        var session = Sessions.GetValue(player, _ => new Session());
        using var guard = await session.Lock.LockAsync().ConfigureAwait(false);
        session.Npc = npc;
        session.Character = player.SelectedCharacter;
        session.Token = string.Empty;
        await ShowAsync(player, session, false).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Handles query, prepare, or one-use confirmation from the menu.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="operation">The requested operation.</param>
    /// <param name="token">The confirmation token, if applicable.</param>
    public static async ValueTask HandleAsync(Player player, string operation, string token = "")
    {
        if (!Sessions.TryGetValue(player, out var session))
        {
            player.Logger.LogWarning("Reset NPC operation {Operation} rejected: no dialog session.", operation);
            return;
        }

        // This lock serializes requests for one player, including token consumption and closing
        // the NPC. Do not call OpenAsync or HandleAsync recursively while holding it.
        using var guard = await session.Lock.LockAsync().ConfigureAwait(false);
        var isCurrentSession = IsCurrentSession(player, session);
        player.Logger.LogInformation("Reset NPC operation {Operation}; current session: {IsCurrentSession}; player state: {PlayerState}.",
            operation, isCurrentSession, player.PlayerState.CurrentState);
        if (!isCurrentSession)
        {
            session.Token = string.Empty;
            return;
        }

        if (operation is "query" or "prepare")
        {
            await ShowAsync(player, session, operation == "prepare", operation == "query").ConfigureAwait(false);
        }
        else if (operation == "confirm")
        {
            await ConfirmAsync(player, session, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets whether this player is in an authorized reset NPC session.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns>Whether the session is open.</returns>
    public static bool IsOpen(Player player) => Sessions.TryGetValue(player, out var session) && IsCurrentSession(player, session);

    private static bool IsCurrentSession(Player player, Session session)
    {
        return player.PlayerState.CurrentState == PlayerState.NpcDialogOpened
            && player.OpenedNpc is { } npc
            && ReferenceEquals(npc, session.Npc)
            && ReferenceEquals(player.SelectedCharacter, session.Character)
            && npc.CurrentMap == player.CurrentMap
            && player.GameContext.PlugInManager.GetActivePlugInsOf<IPlayerTalkToNpcPlugIn>()
                .OfType<ResetCharacterNpcPlugin>().Any(plugIn => plugIn.IsConfiguredNpc(npc));
    }

    private static async ValueTask ShowAsync(Player player, Session session, bool confirmation, bool queried = false)
    {
        session.Token = string.Empty;
        var configuration = player.GameContext.FeaturePlugIns.GetPlugIn<ResetFeaturePlugIn>()?.Configuration;
        var description = configuration is null
            ? player.GetLocalizedMessage(nameof(PlayerMessage.ResetSystemInactive))
            : ResetNpcDescription.Create(player, configuration);
        var feedback = queried ? player.GetLocalizedMessage(nameof(PlayerMessage.ResetNpcQueryRefreshed)) : string.Empty;
        if (confirmation && configuration is not null)
        {
            if (player.Level < configuration.RequiredLevel)
            {
                feedback = player.GetLocalizedMessage(nameof(PlayerMessage.RequiredLevelForReset), configuration.RequiredLevel);
                confirmation = false;
            }
            else
            {
                session.Token = Guid.NewGuid().ToString("N");
                session.QuotedDescription = description;
                session.ExpiresAt = DateTime.UtcNow.AddSeconds(ConfirmationLifetimeSeconds);
                feedback = player.GetLocalizedMessage(nameof(PlayerMessage.ResetNpcReviewConfirmation));
            }
        }

        await player.InvokeViewPlugInAsync<IShowResetDialogPlugIn>(view => view.ShowResetDialogAsync(
                session.Token,
                session.Npc!.Definition.Designation.GetTranslation(player.Culture) ?? string.Empty,
                string.IsNullOrEmpty(feedback) ? description : feedback + "\n" + description,
                player.GetLocalizedMessage(nameof(PlayerMessage.ResetNpcQuery)),
                player.GetLocalizedMessage(confirmation ? nameof(PlayerMessage.ResetNpcConfirm) : nameof(PlayerMessage.ResetNpcApply)),
                player.GetLocalizedMessage(nameof(PlayerMessage.ResetNpcLeave))))
            .ConfigureAwait(false);
    }

    private static async ValueTask ConfirmAsync(Player player, Session session, string token)
    {
        if (string.IsNullOrEmpty(token) || token != session.Token)
        {
            return;
        }

        // Consume before awaiting: a repeated confirmation can never charge a second time.
        session.Token = string.Empty;
        var configuration = player.GameContext.FeaturePlugIns.GetPlugIn<ResetFeaturePlugIn>()?.Configuration;
        if (configuration is null
            || DateTime.UtcNow > session.ExpiresAt
            || ResetNpcDescription.Create(player, configuration) != session.QuotedDescription)
        {
            await ShowAsync(player, session, true).ConfigureAwait(false);
            return;
        }

        await new CloseNpcDialogAction().CloseNpcDialogAsync(player).ConfigureAwait(false);
        await new ResetCharacterAction(player).ResetCharacterAsync().ConfigureAwait(false);
    }

    private sealed class Session
    {
        public AsyncLock Lock { get; } = new();

        public NonPlayerCharacter? Npc { get; set; }

        public Character? Character { get; set; }

        public string Token { get; set; } = string.Empty;

        public string QuotedDescription { get; set; } = string.Empty;

        public DateTime ExpiresAt { get; set; }
    }
}
