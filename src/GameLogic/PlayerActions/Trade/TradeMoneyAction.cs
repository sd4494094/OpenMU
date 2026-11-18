// <copyright file="TradeMoneyAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.Trade;

using MUnique.OpenMU.GameLogic.Views.Inventory;
using MUnique.OpenMU.GameLogic.Views.Trade;

/// <summary>
/// Action to set the traded money.
/// </summary>
public class TradeMoneyAction
{
    /// <summary>
    /// Sets the money which should be traded to the other player.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="moneyAmount">The money amount.</param>
    public async ValueTask TradeMoneyAsync(Player player, uint moneyAmount)
    {
        var tradingPartner = player.TradingPartner;
        using var tradeLock = await TradeSynchronization.LockAsync(player, tradingPartner).ConfigureAwait(false);
        if (!ReferenceEquals(player.TradingPartner, tradingPartner))
        {
            return;
        }

        // Check if Trade is open
        if (player.PlayerState.CurrentState != PlayerState.TradeOpened)
        {
            await player.ShowLocalizedBlueMessageAsync(nameof(PlayerMessage.UncheckTradeAcceptButtonFirst)).ConfigureAwait(false);
            return;
        }

        // An offer replaces the previous reservation, so its money is available again.
        // Calculate before mutating either balance, including for untrusted uint amounts.
        var availableMoney = (long)player.Money + player.TradingMoney;
        var remainingMoney = availableMoney - moneyAmount;
        if (moneyAmount > int.MaxValue
            || remainingMoney < 0
            || remainingMoney > player.GameContext.Configuration.MaximumInventoryMoney)
        {
            return;
        }

        player.Money = (int)remainingMoney;
        player.TradingMoney = (int)moneyAmount;
        await player.InvokeViewPlugInAsync<IUpdateMoneyPlugIn>(p => p.UpdateMoneyAsync()).ConfigureAwait(false);
        await player.InvokeViewPlugInAsync<IRequestedTradeMoneyHasBeenSetPlugIn>(p => p.RequestedTradeMoneyHasBeenSetAsync()).ConfigureAwait(false);

        await player.InvokeViewPlugInAsync<IChangeTradeButtonStatePlugIn>(p => p.ChangeTradeButtonStateAsync(TradeButtonState.Red)).ConfigureAwait(false);
        if (tradingPartner is not null)
        {
            // Send the Money Packet to the Trading Partner
            await tradingPartner.InvokeViewPlugInAsync<ISetTradeMoneyPlugIn>(p => p.SetTradeMoneyAsync(moneyAmount)).ConfigureAwait(false);
            await tradingPartner.PlayerState.TryAdvanceToAsync(PlayerState.TradeOpened).ConfigureAwait(false);
            await tradingPartner.InvokeViewPlugInAsync<IChangeTradeButtonStatePlugIn>(p => p.ChangeTradeButtonStateAsync(TradeButtonState.Red)).ConfigureAwait(false);
        }
    }
}
