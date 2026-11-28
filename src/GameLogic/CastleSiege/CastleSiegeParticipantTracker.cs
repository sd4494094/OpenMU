// <copyright file="CastleSiegeParticipantTracker.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.CastleSiege;

using MUnique.OpenMU.DataModel.Configuration;

/// <summary>
/// Tracks Castle Siege participation and awards the configured post-battle rewards.
/// </summary>
public static class CastleSiegeParticipantTracker
{
    /// <summary>
    /// Records participation for players on the Castle Siege map.
    /// </summary>
    /// <param name="context">The Castle Siege context.</param>
    /// <param name="utcNow">The current UTC time.</param>
    /// <returns>A task that represents the asynchronous tracking operation.</returns>
    public static async ValueTask TrackAsync(CastleSiegeContext context, DateTime utcNow)
    {
        if (context.CurrentState != CastleSiegeState.Start
            || context.Configuration.CastleSiegeMapDefinition is null)
        {
            return;
        }

        foreach (var player in context.GetSiegePlayers())
        {
            UpdateParticipant(context, player, utcNow, true, false);
        }

        await context.SaveParticipationAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Awards eligible participants and increases the winning alliance's guild scores.
    /// </summary>
    /// <param name="context">The Castle Siege context.</param>
    /// <remarks>
    /// The battlefield host runs the guild-score award once for all local game servers.
    /// </remarks>
    /// <returns>A task that represents the asynchronous reward operation.</returns>
    public static async ValueTask AwardRewardsAsync(CastleSiegeContext context)
    {
        var battleStartUtc = context.CurrentBattleStartUtc ?? context.StateStartTimeUtc;
        var eligibleParticipants = context.ParticipantTracking.Values
            .Where(participant => participant.ParticipationTime.TotalSeconds >= context.Configuration.ParticipantRewardMinSeconds)
            .ToList();
        var onlinePlayers = (await context.GetLocalPlayersAsync().ConfigureAwait(false))
            .Where(player => player.SelectedCharacter is not null)
            .GroupBy(player => player.SelectedCharacter!.Id)
            .ToDictionary(group => group.Key, group => group.First());

        if (context.Configuration.RewardItemDefinition is { } rewardDefinition)
        {
            await CastleSiegeRewardDelivery
                .QueueAsync(
                    context.GameContext,
                    eligibleParticipants.Select(participant => participant.CharacterId).ToList(),
                    rewardDefinition,
                    battleStartUtc)
                .ConfigureAwait(false);

            foreach (var participant in eligibleParticipants)
            {
                if (onlinePlayers.TryGetValue(participant.CharacterId, out var player))
                {
                    await CastleSiegePendingRewardPlugIn.DeliverPendingRewardsAsync(player).ConfigureAwait(false);
                }
            }
        }

        if (context.GameContext is IGameServerContext gameServerContext)
        {
            var ownerGuild = context.GetBattleOwner();
            var ownerGuildId = ownerGuild?.PersistentGuildId ?? context.SiegeData.OwnerGuildId;
            var winningGuildIds = await GetPersistedWinningGuildIdsAsync(context, ownerGuildId).ConfigureAwait(false);
            if (winningGuildIds.Length == 0 && ownerGuild is not null)
            {
                winningGuildIds = context.FinalGuildList.Values
                    .Where(guild => guild.Side == ownerGuild.Side)
                    .Select(guild => guild.PersistentGuildId)
                    .ToArray();
            }

            if (winningGuildIds.Length == 0 && ownerGuildId.HasValue)
            {
                winningGuildIds = [ownerGuildId.Value];
            }

            if (ownerGuildId is { } persistentOwnerGuildId)
            {
                context.GameContext.LoggerFactory
                    .CreateLogger(typeof(CastleSiegeParticipantTracker))
                    .LogInformation(
                        "Castle Siege settlement awards {winningGuildCount} guilds {winningGuildIds} for owner {ownerGuildId}; runtime owner available: {runtimeOwnerAvailable}; configured owner score: {ownerScore}; member score: {memberScore}.",
                        winningGuildIds.Length,
                        string.Join(",", winningGuildIds),
                        persistentOwnerGuildId,
                        ownerGuild is not null,
                        context.Configuration.GuildScoreCastleSiege,
                        context.Configuration.GuildScoreCastleSiegeMembers);

                foreach (var guildId in winningGuildIds.Distinct())
                {
                    var score = guildId == persistentOwnerGuildId
                        ? context.Configuration.GuildScoreCastleSiege
                        : context.Configuration.GuildScoreCastleSiegeMembers;
                    if (score <= 0)
                    {
                        continue;
                    }

                    var awardPersisted = await gameServerContext.GuildServer
                        .ApplyCastleSiegeScoreAwardAsync(guildId, battleStartUtc, score)
                        .ConfigureAwait(false);
                    context.GameContext.LoggerFactory
                        .CreateLogger(typeof(CastleSiegeParticipantTracker))
                        .LogInformation(
                            "Castle Siege score award for guild {guildId}, amount {score}, persisted: {awardPersisted}.",
                            guildId,
                            score,
                            awardPersisted);
                    if (!awardPersisted)
                    {
                        throw new InvalidOperationException($"The Castle Siege score award for guild {guildId} could not be persisted.");
                    }
                }
            }
        }
    }

    private static async ValueTask<Guid[]> GetPersistedWinningGuildIdsAsync(CastleSiegeContext context, Guid? ownerGuildId)
    {
        if (ownerGuildId is not { } persistentOwnerGuildId)
        {
            return [];
        }

        using var persistenceContext = context.GameContext.PersistenceContextProvider
            .CreateNewTypedContext(typeof(CastleSiegeData), false, context.GameContext.Configuration);
        var siegeData = await persistenceContext.GetByIdAsync<CastleSiegeData>(context.SiegeData.Id).ConfigureAwait(false);
        if (siegeData?.Guilds.FirstOrDefault(guild => guild.GuildId == persistentOwnerGuildId) is not { } persistedOwner)
        {
            return [];
        }

        return siegeData.Guilds
            .Where(guild => guild.Side == persistedOwner.Side)
            .Select(guild => guild.GuildId)
            .ToArray();
    }

    /// <summary>
    /// Starts tracking a player who entered the Castle Siege map during the battle.
    /// </summary>
    /// <param name="context">The Castle Siege context.</param>
    /// <param name="player">The player.</param>
    /// <param name="utcNow">The current UTC time.</param>
    internal static void StartTracking(CastleSiegeContext context, Player player, DateTime utcNow)
    {
        if (context.CurrentState == CastleSiegeState.Start)
        {
            UpdateParticipant(context, player, utcNow, true, false);
        }
    }

    /// <summary>
    /// Completes the current tracking interval when a player leaves the Castle Siege map.
    /// </summary>
    /// <param name="context">The Castle Siege context.</param>
    /// <param name="player">The player.</param>
    /// <param name="utcNow">The current UTC time.</param>
    internal static void StopTracking(CastleSiegeContext context, Player player, DateTime utcNow)
    {
        if (context.CurrentState == CastleSiegeState.Start)
        {
            UpdateParticipant(context, player, utcNow, false, true);
        }
    }

    private static void UpdateParticipant(
        CastleSiegeContext context,
        Player player,
        DateTime utcNow,
        bool isTracking,
        bool allowPlayerOutsideSiegeMap)
    {
        if ((!allowPlayerOutsideSiegeMap
             && player.CurrentMap?.Definition.Number != context.Configuration.CastleSiegeMapDefinition?.Number)
            || player.SelectedCharacter is not { } character
            || player.GuildStatus is not { } guildStatus
            || (allowPlayerOutsideSiegeMap
                ? context.GetTrackedPlayerJoinSide(player)
                : context.GetPlayerJoinSide(player)) == CastleSiegeJoinSide.None)
        {
            return;
        }

        context.ParticipantTracking.AddOrUpdate(
            character.Id,
            _ => new CastleSiegeParticipant
            {
                CharacterId = character.Id,
                CharacterName = character.Name,
                GuildId = guildStatus.GuildId,
                LastUpdateUtc = utcNow,
                IsTracking = isTracking,
            },
            (_, participant) =>
            {
                var participationTime = participant.ParticipationTime;
                if (participant.IsTracking && utcNow > participant.LastUpdateUtc)
                {
                    participationTime += utcNow - participant.LastUpdateUtc;
                }

                return new CastleSiegeParticipant
                {
                    CharacterId = character.Id,
                    CharacterName = character.Name,
                    GuildId = guildStatus.GuildId,
                    ParticipationTime = participationTime,
                    LastUpdateUtc = utcNow,
                    IsTracking = isTracking,
                };
            });
    }
}
