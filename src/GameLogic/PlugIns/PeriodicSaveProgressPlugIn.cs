// <copyright file="PeriodicSaveProgressPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns;

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Saves the progress of players periodically when their status is 'EnteredWorld'.
/// </summary>
[PlugIn]
[Display(Name = nameof(PlugInResources.PeriodicSaveProgressPlugIn_Name), Description = nameof(PlugInResources.PeriodicSaveProgressPlugIn_Description), ResourceType = typeof(PlugInResources))]
[Guid("CEBBD5BD-B0DF-4768-816D-AF8DF78888B2")]
public class PeriodicSaveProgressPlugIn : IPeriodicTaskPlugIn, ISupportCustomConfiguration<PeriodicSaveProgressPlugInConfiguration>, ISupportDefaultCustomConfiguration
{
    private readonly ConditionalWeakTable<GameContext, SaveSchedule> _schedules = new();

    private long _forceGeneration;

    /// <inheritdoc />
    public PeriodicSaveProgressPlugInConfiguration? Configuration { get; set; }

    /// <inheritdoc />
    public async ValueTask ExecuteTaskAsync(GameContext gameContext)
    {
        // The plug-in manager shares this instance between game servers. A single
        // next-run time lets an empty server consume every other server's interval.
        var schedule = this._schedules.GetValue(gameContext, static _ => new SaveSchedule());
        lock (schedule)
        {
            var generation = Volatile.Read(ref this._forceGeneration);
            if (schedule.IsRunning || (DateTime.UtcNow < schedule.NextRunUtc && schedule.ConsumedForceGeneration == generation))
            {
                return;
            }

            var configuration = this.Configuration ??= CreateDefaultConfiguration();
            schedule.NextRunUtc = DateTime.UtcNow + configuration.Interval;
            schedule.ConsumedForceGeneration = generation;
            schedule.IsRunning = true;
        }

        try
        {
            var logger = gameContext.LoggerFactory.CreateLogger(this.GetType().Name);
            using var scope = LoggerExtensions.BeginScope(logger, gameContext);
            logger.LogDebug("Starting periodical save...");
            try
            {
                var players = await gameContext.GetPlayersAsync().ConfigureAwait(false);
                foreach (var player in players)
                {
                    using var loggerScope = player.Logger.BeginScope(
                        ("Account", player.Account?.LoginName ?? string.Empty),
                        ("Character", player.SelectedCharacter?.Name ?? string.Empty));
                    try
                    {
                        // A packet can open a trade while this tick waits for its persistence lock.
                        // Check eligibility under that same lock, before saving reserved money or items.
                        await player.RunPersistenceExclusiveAsync(async () =>
                        {
                            if (player.PlayerState.CurrentState == PlayerState.EnteredWorld)
                            {
                                await player.SaveProgressAsync().ConfigureAwait(false);
                            }
                            else
                            {
                                player.Logger.LogDebug("Skipping saving the progress periodically for player '{player}' because the state is {state}", player, player.PlayerState.CurrentState);
                            }
                        }).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        player.Logger.LogError(ex, "Error when saving the progress periodically for player '{player}': {ex}", player, ex);
                    }
                }

                logger.LogDebug("Finished periodical save without errors.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error when saving player data periodically.");
            }
        }
        finally
        {
            lock (schedule)
            {
                schedule.IsRunning = false;
            }
        }
    }

    /// <inheritdoc />
    public void ForceStart()
    {
        Interlocked.Increment(ref this._forceGeneration);
    }

    /// <inheritdoc />
    public object CreateDefaultConfig()
    {
        return CreateDefaultConfiguration();
    }

    private static PeriodicSaveProgressPlugInConfiguration CreateDefaultConfiguration()
    {
        return new PeriodicSaveProgressPlugInConfiguration();
    }

    /// <summary>
    /// Tracks a context without keeping a disposed game server alive through the shared plug-in.
    /// </summary>
    private sealed class SaveSchedule
    {
        /// <summary>
        /// Gets or sets the next regular run time.
        /// </summary>
        public DateTime NextRunUtc { get; set; }

        /// <summary>
        /// Gets or sets the last force request consumed by this context.
        /// </summary>
        public long ConsumedForceGeneration { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether a save pass is still running.
        /// </summary>
        public bool IsRunning { get; set; }
    }
}
