// <copyright file="QuestCompletionAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.Quests;

using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel.Configuration.Quests;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Views.Character;
using MUnique.OpenMU.GameLogic.Views.Inventory;
using MUnique.OpenMU.GameLogic.Views.Quest;
using MUnique.OpenMU.Persistence;

/// <summary>
/// A player action which implements the completion of a quest.
/// </summary>
public class QuestCompletionAction
{
    /// <summary>
    /// Tries to completes the quest of the given group and number for the specified player.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="group">The group.</param>
    /// <param name="number">The number.</param>
    public ValueTask CompleteQuestAsync(Player player, short group, short number)
        => player.RunPersistenceExclusiveAsync(() => this.CompleteQuestCoreAsync(player, group, number));

    private static async ValueTask AddRewardAsync(Player player, QuestReward reward, QuestDefinition quest, ICollection<Func<ValueTask>> publishRewards)
    {
        switch (reward.RewardType)
        {
            case QuestRewardType.Attribute:
                var attribute = player.SelectedCharacter!.Attributes.FirstOrDefault(a => object.Equals(a.Definition, reward.AttributeReward));
                if (attribute is null)
                {
                    attribute = player.PersistenceContext.CreateNew<StatAttribute>(reward.AttributeReward, 0);
                    player.SelectedCharacter.Attributes.Add(attribute);
                }

                attribute.Value += reward.Value;

                // Compensate level-up points when doing a quest at a later level.
                if (attribute.Definition == Stats.PointsPerLevelUp)
                {
                    var playerLevel = (int)player.Attributes![Stats.Level];
                    player.SelectedCharacter.LevelUpPoints += (playerLevel - quest.MinimumCharacterLevel) * reward.Value;
                }

                // The legacy view derives compensation from the active quest's minimum level.
                // Keep this auxiliary notification before ClearAsync; final completion still waits
                // for persistence, and an unconfirmed outcome cannot remain in a playable session.
                await player.InvokeViewPlugInAsync<ILegacyQuestRewardPlugIn>(p => p.ShowAsync(player, QuestRewardType.Attribute, reward.Value, attribute.Definition)).ConfigureAwait(false);
                break;
            case QuestRewardType.Item:
                var item = player.PersistenceContext.CreateNew<Item>();
                item.AssignValues(reward.ItemReward ?? throw new InvalidOperationException($"Reward {reward.GetId()} is defined as item reward, but has no item assigned"));
                if (await player.Inventory!.AddItemAsync(item).ConfigureAwait(false))
                {
                    publishRewards.Add(() => player.InvokeViewPlugInAsync<IItemAppearPlugIn>(p => p.ItemAppearAsync(item)));
                }
                else if (player.CurrentMap is { HostServer: not null } currentMap)
                {
                    // The address, generation and original deadlines are committed
                    // with quest completion, before another player can see the reward.
                    currentMap.StageGroundDelivery(item, player.PersistenceContext, player.SelectedCharacter!.GetId(), player.Position);
                    publishRewards.Add(async () =>
                    {
                        player.PersistenceContext.Detach(item);
                        await currentMap.PublishGroundItemAsync(item, player.GameContext, player).ConfigureAwait(false);
                    });
                }
                else
                {
                    // A custom context without an actual map host cannot address a
                    // recoverable ground drop. Retain the reward on the character.
                    var recovery = player.SelectedCharacter!.RecoveryStorage ??= player.PersistenceContext.CreateNew<ItemStorage>();
                    recovery.Items.Add(item);
                    player.Logger.LogWarning("Retaining quest reward {Item} for {Player}: no actual ground host is available.", item, player);
                }

                break;
            case QuestRewardType.LevelUpPoints:
                player.SelectedCharacter!.LevelUpPoints += reward.Value;
                publishRewards.Add(() => player.InvokeViewPlugInAsync<ILegacyQuestRewardPlugIn>(p => p.ShowAsync(player, QuestRewardType.LevelUpPoints, reward.Value, null)));
                break;
            case QuestRewardType.CharacterEvolutionFirstToSecond:
                player.SelectedCharacter!.CharacterClass = player.SelectedCharacter.CharacterClass?.NextGenerationClass
                                                           ?? throw new InvalidOperationException($"Current character class has no next generation");
                publishRewards.Add(() => player.ForEachWorldObserverAsync<ILegacyQuestRewardPlugIn>(
                    p => p.ShowAsync(
                        player,
                        QuestRewardType.CharacterEvolutionFirstToSecond,
                        reward.Value,
                        null),
                    true));
                break;
            case QuestRewardType.CharacterEvolutionSecondToThird:
                player.SelectedCharacter!.CharacterClass = player.SelectedCharacter.CharacterClass?.NextGenerationClass
                                                           ?? throw new InvalidOperationException($"Current character class has no next generation");
                publishRewards.Add(() => player.ForEachWorldObserverAsync<ILegacyQuestRewardPlugIn>(
                    p => p.ShowAsync(
                        player,
                        QuestRewardType.CharacterEvolutionSecondToThird,
                        reward.Value,
                        null),
                    true));
                publishRewards.Add(() => player.InvokeViewPlugInAsync<IUpdateMasterStatsPlugIn>(p => p.SendMasterStatsAsync()));
                break;
            case QuestRewardType.Experience:
                await player.AddExperienceAsync(reward.Value, null).ConfigureAwait(false);
                break;
            case QuestRewardType.Money:
                player.TryAddMoney(reward.Value);
                publishRewards.Add(() => player.InvokeViewPlugInAsync<IUpdateMoneyPlugIn>(p => p.UpdateMoneyAsync()));
                break;
            case QuestRewardType.GensAttribution:
                // not yet implemented.
                break;
            case QuestRewardType.Skill:
                if (reward.SkillReward is not { } skill)
                {
                    player.Logger.LogError("Reward has no skill defined.");
                    return;
                }

                if (player.SkillList is not { } skillList)
                {
                    player.Logger.LogError("Can't reward the skill; SkillList is null.");
                    return;
                }

                if (!skillList.ContainsSkill(skill.Number.ToUnsigned()))
                {
                    await skillList.AddLearnedSkillAsync(skill).ConfigureAwait(false);
                }
                else
                {
                    player.Logger.LogWarning($"Skill {skill} is already learned.");
                }

                break;
            default:
                player.Logger.LogWarning("Unknown reward type: {0}", reward.RewardType);
                break;
        }
    }

    private async ValueTask CompleteQuestCoreAsync(Player player, short group, short number)
    {
        if (player.SelectedCharacter is null
            || player.PlayerState.CurrentState == PlayerState.Disconnected
            || player.PlayerState.CurrentState == PlayerState.Finished)
        {
            return;
        }

        using var loggerScope = player.Logger.BeginScope(this.GetType());
        var questState = player.GetQuestState(group, number);
        var activeQuest = questState?.ActiveQuest;
        if (activeQuest is null)
        {
            player.Logger.LogDebug("Failed, no active quest");
            return;
        }

        foreach (var requiredItem in activeQuest.RequiredItems)
        {
            var requiredLevel = requiredItem.DropItemGroup?.ItemLevel;
            var itemCount = player.Inventory?.Items.Count(requiredItem.Matches);

            if (requiredItem.MinimumNumber <= itemCount)
            {
                continue;
            }

            player.Logger.LogDebug("Failed, required item not found: {0}", requiredItem.Item!.GetNameForLevel(requiredLevel ?? 0));
            return;
        }

        foreach (var requiredKills in activeQuest.RequiredMonsterKills)
        {
            var currentKillCount = questState!.RequirementStates.FirstOrDefault(r => object.Equals(r.Requirement, requiredKills))?.KillCount ?? 0;
            if (currentKillCount >= requiredKills.MinimumNumber)
            {
                continue;
            }

            player.Logger.LogDebug("Failed, required kills of monster {0}: {1}/{2};", requiredKills.Monster?.Designation, currentKillCount, requiredKills.MinimumNumber);
            return;
        }

        if (activeQuest.RequiresClientAction && !questState!.ClientActionPerformed)
        {
            player.Logger.LogDebug("Failed, client action not performed.");
            return;
        }

        if (activeQuest.EvolvesIntoInactiveClass(player))
        {
            player.Logger.LogDebug("Failed, the quest evolves into the inactive class {0}.", player.SelectedCharacter?.CharacterClass?.NextGenerationClass);
            return;
        }

        questState!.LastFinishedQuest = activeQuest;
        foreach (var requiredItem in activeQuest.RequiredItems)
        {
            var items = player.Inventory!.Items
                .Where(requiredItem.Matches)
                .Take(requiredItem.MinimumNumber)
                .ToList();

            foreach (var item in items)
            {
                await player.DestroyInventoryItemAsync(item).ConfigureAwait(false);
            }
        }

        var publishRewards = new List<Func<ValueTask>>();
        foreach (var reward in activeQuest.Rewards)
        {
            await AddRewardAsync(player, reward, activeQuest, publishRewards).ConfigureAwait(false);
        }

        await questState.ClearAsync(player.PersistenceContext).ConfigureAwait(false);
        if (!await player.SaveQuestOutcomeAsync().ConfigureAwait(false))
        {
            return;
        }

        foreach (var publishReward in publishRewards)
        {
            await publishReward().ConfigureAwait(false);
        }

        await player.InvokeViewPlugInAsync<IQuestCompletionResponsePlugIn>(p => p.QuestCompletedAsync(activeQuest)).ConfigureAwait(false);
    }
}
