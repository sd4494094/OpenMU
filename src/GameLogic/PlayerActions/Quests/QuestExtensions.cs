// <copyright file="QuestExtensions.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.Quests;

using MUnique.OpenMU.Persistence;

/// <summary>
/// Extensions regarding quests.
/// </summary>
public static class QuestExtensions
{
    /// <summary>
    /// Clears the quest state for the specified player.
    /// </summary>
    /// <param name="questState">State of the quest.</param>
    /// <param name="persistenceContext">The persistence context of the player.</param>
    public static async ValueTask ClearAsync(this CharacterQuestState questState, IContext persistenceContext)
    {
        questState.ActiveQuest = null;
        questState.ClientActionPerformed = false;
        foreach (var requirementState in questState.RequirementStates)
        {
            await persistenceContext.DeleteAsync(requirementState).ConfigureAwait(false);
        }

        questState.RequirementStates.Clear();
    }

    /// <summary>
    /// Confirms the quest outcome before success is published, closing an uncertain session without reapplying the outcome.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns>Whether persistence confirmed the outcome.</returns>
    internal static async ValueTask<bool> SaveQuestOutcomeAsync(this Player player)
    {
        try
        {
            if (await player.SaveProgressAsync().ConfigureAwait(false))
            {
                return true;
            }

            player.Logger.LogError("The quest outcome for player {Player} was not saved; disconnecting.", player);
        }
        catch (Exception exception)
        {
            player.Logger.LogError(exception, "Couldn't confirm saving the quest outcome for player {Player}; disconnecting.", player);
        }

        // A failed commit acknowledgement is ambiguous. Teardown retries the same tracked
        // outcome under the persistence gate; refunding or reapplying it could duplicate rewards.
        // A new session loads the durable result, even when teardown cannot confirm its save.
        await player.DisconnectAsync().ConfigureAwait(false);
        return false;
    }
}
