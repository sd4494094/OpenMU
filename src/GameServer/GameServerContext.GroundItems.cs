// <copyright file="GameServerContext.GroundItems.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer;

using MUnique.OpenMU.GameLogic;

/// <summary>
/// Runs durable ground recovery at startup, map readiness and during host maintenance.
/// </summary>
public partial class GameServerContext
{
    private readonly GroundItemRecovery _groundItemRecovery;

    /// <summary>
    /// Starts the initial host recovery before listeners open.
    /// </summary>
    /// <returns>The startup scan.</returns>
    internal ValueTask StartGroundItemRecoveryAsync() => this._groundItemRecovery.StartAsync();

    /// <summary>
    /// Stops new recovery scans and joins the current scan before shutdown.
    /// </summary>
    /// <returns>The stop operation.</returns>
    internal ValueTask StopGroundItemRecoveryAsync() => this._groundItemRecovery.StopAsync();

    /// <inheritdoc />
    protected override async ValueTask InitializeMapAsync(GameMap map)
    {
        await base.InitializeMapAsync(map).ConfigureAwait(false);
        await this._groundItemRecovery.RestoreMapAsync(map).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override ValueTask ExecuteAdditionalPeriodicTasksAsync()
        => this._groundItemRecovery?.ScanAsync() ?? ValueTask.CompletedTask;

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        this.StopPeriodicTasks();
        await this._groundItemRecovery.StopAsync().ConfigureAwait(false);
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }
}
