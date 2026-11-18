// <copyright file="RequestInventoryAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.Character;

using MUnique.OpenMU.GameLogic.Views.Inventory;

/// <summary>
/// Refreshes a character inventory without changing storage or dialog state.
/// </summary>
public class RequestInventoryAction
{
    /// <summary>
    /// Sends the current inventory when the character is in the world and no temporary dialog is open.
    /// </summary>
    /// <param name="player">The requesting player.</param>
    public async ValueTask RequestInventoryAsync(Player player)
    {
        if (player.PlayerState.CurrentState != PlayerState.EnteredWorld
            || player.SelectedCharacter is null
            || player.Inventory is null)
        {
            return;
        }

        await player.InvokeViewPlugInAsync<IUpdateInventoryListPlugIn>(p => p.UpdateInventoryListAsync()).ConfigureAwait(false);
    }
}
