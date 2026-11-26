// <copyright file="TradeAcceptAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.Trade;

using MUnique.OpenMU.GameLogic.Views.Trade;

/// <summary>
/// Action to accept the trade.
/// </summary>
public class TradeAcceptAction : BaseTradeAction
{
    /// <summary>
    /// Handles the trade request answer.
    /// </summary>
    /// <param name="tradeAccepter">The trader which answers the trade request.</param>
    /// <param name="accept">Indicates if the trader accepted the trade request.</param>
    public async ValueTask HandleTradeAcceptAsync(ITrader tradeAccepter, bool accept)
    {
        var tradePartner = tradeAccepter.TradingPartner;
        if (tradeAccepter is Player player)
        {
            if (tradePartner is Player partner)
            {
                await player.RunPersistenceExclusiveAsync(
                    partner,
                    () => this.HandleTradeAcceptCoreAsync(tradeAccepter, tradePartner, accept)).ConfigureAwait(false);
            }
            else
            {
                await player.RunPersistenceExclusiveAsync(
                    () => this.HandleTradeAcceptCoreAsync(tradeAccepter, tradePartner, accept)).ConfigureAwait(false);
            }
        }
        else
        {
            await this.HandleTradeAcceptCoreAsync(tradeAccepter, tradePartner, accept).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Rejects an opening before inventories or trade windows have been prepared.
    /// </summary>
    /// <param name="accepter">The accepting trader.</param>
    /// <param name="requester">The requesting trader.</param>
    private static async ValueTask RejectTradeOpeningAsync(ITrader accepter, ITrader requester)
    {
        await accepter.PlayerState.TryAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false);
        await requester.PlayerState.TryAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false);
        accepter.TradingPartner = null;
        requester.TradingPartner = null;

        // No offer or inventory backup exists yet. Normal cancellation clears the temporary
        // storage, which can still hold items left by an earlier window at this point.
        await accepter.InvokeViewPlugInAsync<IShowTradeRequestAnswerPlugIn>(p => p.ShowTradeRequestAnswerAsync(false)).ConfigureAwait(false);
        await requester.InvokeViewPlugInAsync<IShowTradeRequestAnswerPlugIn>(p => p.ShowTradeRequestAnswerAsync(false)).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns earlier temporary items without leaving either inventory partially prepared.
    /// </summary>
    /// <param name="accepter">The accepting trader.</param>
    /// <param name="requester">The requesting trader.</param>
    /// <returns>Whether both temporary storages were emptied into their owners' inventories.</returns>
    private static async ValueTask<bool> TryReturnEarlierTemporaryItemsAsync(ITrader accepter, ITrader requester)
    {
        var preparations = new[] { accepter, requester }
            .Select(trader => (
                Trader: trader,
                Items: trader.TemporaryStorage?.Items.Select(item => (Item: item, Slot: item.ItemSlot)).ToArray() ?? []))
            .ToArray();
        var returned = false;
        try
        {
            foreach (var (trader, items) in preparations)
            {
                if (items.Length > 0 && !await trader.Inventory!.TryTakeAllAsync(trader.TemporaryStorage!).ConfigureAwait(false))
                {
                    trader.Logger.LogWarning("Could not return the items of the temporary storage of {trader} to the inventory before opening a trade.", trader);
                    return false;
                }
            }

            returned = true;
            return true;
        }
        finally
        {
            if (!returned)
            {
                // Both persistence and trade locks are held, so these original temporary slots
                // cannot be reused while we undo the first trader's successful preparation.
                foreach (var (trader, items) in preparations)
                {
                    foreach (var (item, slot) in items)
                    {
                        if (trader.TemporaryStorage!.Items.Contains(item))
                        {
                            continue;
                        }

                        await trader.Inventory!.RemoveItemAsync(item).ConfigureAwait(false);
                        if (!await trader.TemporaryStorage.AddItemAsync(slot, item).ConfigureAwait(false))
                        {
                            throw new InvalidOperationException("Could not restore temporary items before rejecting a trade opening.");
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Opens a trade after both players have been saved and their earlier temporary items returned.
    /// </summary>
    /// <param name="trader">Trader for which the trade should be opened.</param>
    private async ValueTask OpenPreparedTradeAsync(ITrader trader)
    {
        trader.BackupInventory = new BackupItemStorage(trader.Inventory!.ItemStorage);
        trader.TradingMoney = 0;
        await trader.InvokeViewPlugInAsync<IShowTradeRequestAnswerPlugIn>(p => p.ShowTradeRequestAnswerAsync(true)).ConfigureAwait(false);
    }

    private async ValueTask HandleTradeAcceptCoreAsync(ITrader tradeAccepter, ITrader? tradePartner, bool accept)
    {
        using var tradeLock = await TradeSynchronization.LockAsync(tradeAccepter, tradePartner).ConfigureAwait(false);
        if (!ReferenceEquals(tradeAccepter.TradingPartner, tradePartner))
        {
            return;
        }

        if (accept && tradePartner != null)
        {
            if (!await tradeAccepter.PlayerState.TryAdvanceToAsync(PlayerState.TradeOpened).ConfigureAwait(false) || !await tradePartner.PlayerState.TryAdvanceToAsync(PlayerState.TradeOpened).ConfigureAwait(false))
            {
                ////Something bad happened here...
                await this.CancelTradeAsync(tradeAccepter).ConfigureAwait(false);
                await this.CancelTradeAsync(tradePartner).ConfigureAwait(false);
                await tradePartner.InvokeViewPlugInAsync<IShowTradeRequestAnswerPlugIn>(p => p.ShowTradeRequestAnswerAsync(false)).ConfigureAwait(false);
                await tradeAccepter.InvokeViewPlugInAsync<IShowTradeRequestAnswerPlugIn>(p => p.ShowTradeRequestAnswerAsync(false)).ConfigureAwait(false);
            }
            else
            {
                var ready = false;
                try
                {
                    // Neither player may see success before every transferable item is durable.
                    ready = await tradeAccepter.SaveProgressAsync().ConfigureAwait(false)
                        && await tradePartner.SaveProgressAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    tradeAccepter.Logger.LogError(ex, "Error saving progress before opening a trade between {tradeAccepter} and {tradePartner}", tradeAccepter, tradePartner);
                }

                if (ready)
                {
                    ready = false;
                    try
                    {
                        ready = await TryReturnEarlierTemporaryItemsAsync(tradeAccepter, tradePartner).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        tradeAccepter.Logger.LogError(ex, "Error returning earlier temporary items before opening a trade between {tradeAccepter} and {tradePartner}", tradeAccepter, tradePartner);
                    }
                }

                if (!ready)
                {
                    await RejectTradeOpeningAsync(tradeAccepter, tradePartner).ConfigureAwait(false);
                    return;
                }

                try
                {
                    await this.OpenPreparedTradeAsync(tradeAccepter).ConfigureAwait(false);
                    await this.OpenPreparedTradeAsync(tradePartner).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    tradeAccepter.Logger.LogError(ex, "Error while opening a trade between {tradeAccepter} and {tradePartner}", tradeAccepter, tradePartner);
                    await this.CancelTradeAsync(tradeAccepter).ConfigureAwait(false);
                    await this.CancelTradeAsync(tradePartner).ConfigureAwait(false);
                }
            }
        }
        else
        {
            if (tradePartner != null)
            {
                await this.CancelTradeAsync(tradePartner).ConfigureAwait(false);
                await tradePartner.InvokeViewPlugInAsync<IShowTradeRequestAnswerPlugIn>(p => p.ShowTradeRequestAnswerAsync(false)).ConfigureAwait(false);
            }

            await this.CancelTradeAsync(tradeAccepter).ConfigureAwait(false);
            await tradeAccepter.InvokeViewPlugInAsync<IShowTradeRequestAnswerPlugIn>(p => p.ShowTradeRequestAnswerAsync(false)).ConfigureAwait(false);
        }
    }
}
