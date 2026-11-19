// <copyright file="MuHelper.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.MuHelper;

using System.Threading;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.PlugIns.ChatCommands;
using MUnique.OpenMU.GameLogic.Views.MuHelper;
using Nito.AsyncEx;

/// <summary>
/// Implements the logic of the 'MU Helper'.
/// </summary>
public class MuHelper : AsyncDisposable
{
    /// <summary>
    /// The <see cref="IElement"/> which is added to the players <see cref="IAttributeSystem"/>
    /// when the MU Helper is active.
    /// </summary>
    private static readonly IElement ActiveElement = new ConstantElement(1);

    /// <summary>
    /// Associated player.
    /// </summary>
    private readonly Player _player;

    /// <summary>
    /// The current configuration.
    /// </summary>
    private readonly MuHelperConfiguration _configuration;

    private readonly AsyncLock _lifecycleLock = new();

    private CancellationTokenSource? _stopCts;
    private Task? _runTask;
    private DateTime _startTimestamp;

    /// <summary>
    /// Initializes a new instance of the <see cref="MuHelper"/> class.
    /// </summary>
    /// <param name="player">current player.</param>
    public MuHelper(Player player)
    {
        this._player = player;
        this._configuration = this._player.GameContext.FeaturePlugIns.GetPlugIn<MuHelperFeaturePlugIn>()?.Configuration ?? new MuHelperConfiguration();
    }

    /// <summary>
    /// Start Mu Helper.
    /// </summary>
    public async ValueTask<bool> TryStartAsync()
    {
        using var gate = await this._lifecycleLock.LockAsync().ConfigureAwait(false);
        if (this._runTask is { IsCompleted: false })
        {
            await this._player.ShowLocalizedBlueMessageAsync(nameof(PlayerMessage.MuHelperAlreadyRunning)).ConfigureAwait(false);
            return false;
        }

        if (this._player.Level < this._configuration.MinLevel)
        {
            await this._player.ShowLocalizedBlueMessageAsync(nameof(PlayerMessage.MuHelperMinimumLevel), this._configuration.MinLevel).ConfigureAwait(false);
            return false;
        }

        if (this._player.Level > this._configuration.MaxLevel)
        {
            await this._player.ShowLocalizedBlueMessageAsync(nameof(PlayerMessage.MuHelperMaximumLevel), this._configuration.MaxLevel).ConfigureAwait(false);
            return false;
        }

        this._startTimestamp = DateTime.UtcNow;
        var requiredMoney = this.CalculateRequiredMoney();

        if (!this._player.TryRemoveMoney(requiredMoney))
        {
            await this._player.ShowLocalizedBlueMessageAsync(nameof(PlayerMessage.MuHelperRequiresMoney), requiredMoney).ConfigureAwait(false);
            return false;
        }

        await this._player.InvokeViewPlugInAsync<IMuHelperStatusUpdatePlugIn>(p => p.StartAsync()).ConfigureAwait(false);
        await this._player.InvokeViewPlugInAsync<IMuHelperStatusUpdatePlugIn>(p => p.ConsumeMoneyAsync((uint)requiredMoney)).ConfigureAwait(false);

        this._player.Attributes?.AddElement(ActiveElement, Stats.IsMuHelperActive);
        this._stopCts?.Dispose();
        this._stopCts = new CancellationTokenSource();
        this._runTask = this.RunLoopAsync(this._stopCts.Token);
        return true;
    }

    /// <summary>
    /// Stops the MU Helper.
    /// </summary>
    public async ValueTask StopAsync()
    {
        using var gate = await this._lifecycleLock.LockAsync().ConfigureAwait(false);
        if (this._runTask is not { } runTask || this._stopCts is not { } stopCts)
        {
            return;
        }

        await stopCts.CancelAsync().ConfigureAwait(false);
        await runTask.ConfigureAwait(false);
        this._runTask = null;
        this._stopCts = null;
        stopCts.Dispose();
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        await this.StopAsync().ConfigureAwait(false);
    }

    private int CalculateRequiredMoney() =>
        MuHelperZenCostCalculator.Calculate(this._player, this._configuration, this._startTimestamp);

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (this._configuration.PayInterval <= TimeSpan.Zero)
            {
                this._player.Logger.LogDebug("MU Helper PayInterval is {PayInterval}. Stopping for {CharacterName}.", this._configuration.PayInterval, this._player.Name);
                return;
            }

            using var timer = new PeriodicTimer(this._configuration.PayInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await this.TryCollectAsync().ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // StopAsync requested cancellation.
        }
        catch (Exception ex)
        {
            this._player.Logger.LogWarning(ex, "MU Helper billing failed for {CharacterName}.", this._player.Name);
        }
        finally
        {
            // The billing task owns this cleanup. Calling StopAsync here would wait for this task itself.
            try
            {
                this._player.Attributes?.RemoveElement(ActiveElement, Stats.IsMuHelperActive);
                if (this._player.IsConnected)
                {
                    await this._player.InvokeViewPlugInAsync<IMuHelperStatusUpdatePlugIn>(p => p.StopAsync()).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                this._player.Logger.LogWarning(ex, "MU Helper cleanup failed for {CharacterName}.", this._player.Name);
            }
        }
    }

    /// <summary>
    /// Collects the fee for the current interval.
    /// </summary>
    /// <returns>Whether the fee was paid and the helper can keep running.</returns>
    private async ValueTask<bool> TryCollectAsync()
    {
        var amount = this.CalculateRequiredMoney();
        if (amount <= 0 || !this._player.TryRemoveMoney(amount))
        {
            return false;
        }

        await this._player.InvokeViewPlugInAsync<IMuHelperStatusUpdatePlugIn>(p => p.ConsumeMoneyAsync((uint)amount)).ConfigureAwait(false);
        return true;
    }
}