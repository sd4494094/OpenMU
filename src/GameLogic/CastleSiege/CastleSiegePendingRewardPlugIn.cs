// <copyright file="CastleSiegePendingRewardPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.CastleSiege;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Delivers pending Castle Siege participant rewards when a character enters the game.
/// </summary>
[PlugIn]
[Display(Name = nameof(PlugInResources.CastleSiegePendingRewardPlugIn_Name), Description = nameof(PlugInResources.CastleSiegePendingRewardPlugIn_Description), ResourceType = typeof(PlugInResources))]
[Guid("EA4DB403-84F2-4E31-B4D4-441B3EADFCB6")]
public class CastleSiegePendingRewardPlugIn : IPlayerStateChangedPlugIn
{
    /// <inheritdoc />
    public async ValueTask PlayerStateChangedAsync(Player player, State previousState, State currentState)
    {
        if (previousState != PlayerState.CharacterSelection
            || currentState != PlayerState.EnteredWorld
            || player.SelectedCharacter is not { } character)
        {
            return;
        }

        await DeliverPendingRewardsAsync(player).ConfigureAwait(false);
    }

    /// <summary>
    /// Delivers outstanding Castle Siege rewards for an online player.
    /// </summary>
    /// <param name="player">The player whose pending rewards are delivered.</param>
    internal static async ValueTask DeliverPendingRewardsAsync(Player player)
    {
        if (player.SelectedCharacter is not { } character)
        {
            return;
        }

        await player.RunPersistenceExclusiveAsync(async () =>
        {
            var pendingRewards = await player.PersistenceContext
                .GetPendingCastleSiegeRewardsAsync(character.Id)
                .ConfigureAwait(false);
            var hasChanges = false;
            foreach (var pendingReward in pendingRewards.Where(reward => !reward.IsDelivered))
            {
                var rewardDefinition = player.GameContext.Configuration.Items
                    .FirstOrDefault(item => item.GetId() == pendingReward.ItemDefinitionId);
                if (rewardDefinition is null)
                {
                    if (pendingReward.BattleStartUtc is null)
                    {
                        await player.PersistenceContext.DeleteAsync(pendingReward).ConfigureAwait(false);
                        hasChanges = true;
                    }

                    player.Logger.LogWarning(
                        "Castle Siege reward {RewardId} references unavailable item definition {ItemDefinitionId}.",
                        pendingReward.Id,
                        pendingReward.ItemDefinitionId);
                    continue;
                }

                if (!await CastleSiegeRewardDelivery
                        .TryAddToInventoryAsync(player, rewardDefinition)
                        .ConfigureAwait(false))
                {
                    continue;
                }

                if (pendingReward.BattleStartUtc is null)
                {
                    await player.PersistenceContext.DeleteAsync(pendingReward).ConfigureAwait(false);
                }
                else
                {
                    pendingReward.IsDelivered = true;
                }

                hasChanges = true;
            }

            if (hasChanges)
            {
                // The item creation and delivery receipt are committed together by the player's context.
                await player.SaveProgressAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }
}
