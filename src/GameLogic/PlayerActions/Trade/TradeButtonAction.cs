// <copyright file="TradeButtonAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.Trade;

using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.Views.Trade;
using MUnique.OpenMU.Persistence;

/// <summary>
/// Action to change the trade button state.
/// </summary>
public class TradeButtonAction : BaseTradeAction
{
    /// <summary>
    /// Tries to change the trade button change to the new <paramref name="tradeButtonState"/>.
    /// </summary>
    /// <param name="trader">The trader.</param>
    /// <param name="tradeButtonState">The new state of the trade button.</param>
    public async ValueTask TradeButtonChangedAsync(ITrader trader, TradeButtonState tradeButtonState)
    {
        using var loggerScope = (trader as Player)?.Logger.BeginScope(this.GetType());
        var tradingPartner = trader.TradingPartner;
        using var tradeLock = await TradeSynchronization.LockAsync(trader, tradingPartner).ConfigureAwait(false);
        if (!ReferenceEquals(trader.TradingPartner, tradingPartner))
        {
            return;
        }

        var success = (tradeButtonState == TradeButtonState.Checked && await trader.PlayerState.TryAdvanceToAsync(PlayerState.TradeButtonPressed).ConfigureAwait(false))
                      || (tradeButtonState == TradeButtonState.Unchecked && await trader.PlayerState.TryAdvanceToAsync(PlayerState.TradeOpened).ConfigureAwait(false));
        if (!success)
        {
            return;
        }

        if (trader.PlayerState.CurrentState == PlayerState.TradeButtonPressed
            && tradingPartner is not null
            && tradingPartner.PlayerState.CurrentState == PlayerState.TradeButtonPressed)
        {
            TradeResult result = await this.InternalFinishTradeAsync(trader, tradingPartner).ConfigureAwait(false);
            if (result != TradeResult.Success)
            {
                await this.CancelTradeAsync(tradingPartner, false).ConfigureAwait(false);
                await this.CancelTradeAsync(trader, false).ConfigureAwait(false);
                (trader as Player)?.Logger.LogDebug($"Cancelled the trade because of unfinished state. trader: {trader.Name}, partner:{tradingPartner.Name}");
            }

            await trader.InvokeViewPlugInAsync<ITradeFinishedPlugIn>(p => p.TradeFinishedAsync(result)).ConfigureAwait(false);
            await tradingPartner.InvokeViewPlugInAsync<ITradeFinishedPlugIn>(p => p.TradeFinishedAsync(result)).ConfigureAwait(false);
        }
        else if (tradingPartner is not null)
        {
            await tradingPartner.InvokeViewPlugInAsync<IChangeTradeButtonStatePlugIn>(p => p.ChangeTradeButtonStateAsync(TradeButtonState.Checked)).ConfigureAwait(false);
        }
        else
        {
            // nothing to do.
        }
    }

    private static async ValueTask<bool> TryAddItemsOfTradingPartnerAsync(ITrader trader)
    {
        if (trader.TradingPartner?.TemporaryStorage?.Items.Any() ?? false)
        {
            return await trader.Inventory!.TryTakeAllAsync(trader.TradingPartner.TemporaryStorage!).ConfigureAwait(false);
        }

        return true;
    }

    private static ItemStorage GetPersistentInventory(ITrader trader)
    {
        var storage = trader.Inventory!.ItemStorage;
        while (storage is ItemStorageAdapter adapter)
        {
            storage = adapter.ActualStorage;
        }

        return storage;
    }

    private async ValueTask<TradeResult> InternalFinishTradeAsync(ITrader trader, ITrader tradingPartner)
    {
        await using var context = await trader.PlayerState.TryBeginAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false);
        await using var partnerContext = await tradingPartner.PlayerState.TryBeginAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false);
        if (!context.Allowed || !partnerContext.Allowed)
        {
            context.Allowed = false;
            partnerContext.Allowed = false;
            (trader as Player)?.Logger.LogDebug($"Unexpected player states. {trader.Name}:{trader.PlayerState}, {tradingPartner.Name}:{tradingPartner.PlayerState}");
            return TradeResult.Cancelled;
        }

        // Offers have already been reserved from each inventory. Validate both final
        // balances before attaching or transferring items, including configured limits
        // below int.MaxValue. Use long so overflow is an ordinary rejection, too.
        var traderMoney = (long)trader.Money + tradingPartner.TradingMoney;
        var partnerMoney = (long)tradingPartner.Money + trader.TradingMoney;
        if (traderMoney < 0 || traderMoney > trader.GameContext.Configuration.MaximumInventoryMoney
            || partnerMoney < 0 || partnerMoney > tradingPartner.GameContext.Configuration.MaximumInventoryMoney)
        {
            (trader as Player)?.Logger.LogDebug("Cancelled trade because a final balance exceeds the inventory money limits. Trader: {Trader}, partner: {Partner}", trader.Name, tradingPartner.Name);
            return TradeResult.Cancelled;
        }

        using var itemContext = trader.GameContext.PersistenceContextProvider.CreateNewTradeContext();
        var traderItems = trader.TemporaryStorage!.Items.ToList();
        var tradePartnerItems = tradingPartner.TemporaryStorage!.Items.ToList();

        // Checked here, and not only when an item is moved into the trade: the temporary storage
        // is shared with other windows (e.g. the chaos machine), so it may already contain items.
        // Both are checked, so that each trader gets the message about their own items.
        var traderItemsAreTradable = await this.CheckItemsAreTradableAsync(trader, traderItems).ConfigureAwait(false);
        var partnerItemsAreTradable = await this.CheckItemsAreTradableAsync(tradingPartner, tradePartnerItems).ConfigureAwait(false);
        if (!traderItemsAreTradable || !partnerItemsAreTradable)
        {
            return TradeResult.Cancelled;
        }

        this.AttachItemsToPersistenceContext(traderItems, itemContext);
        this.AttachItemsToPersistenceContext(tradePartnerItems, itemContext);

        if (!await TryAddItemsOfTradingPartnerAsync(trader).ConfigureAwait(false) || !await TryAddItemsOfTradingPartnerAsync(tradingPartner).ConfigureAwait(false))
        {
            // TODO: Maybe notify which player has the full inventory.
            await this.SendMessageAsync(trader, nameof(PlayerMessage.InventoryFull)).ConfigureAwait(false);
            await this.SendMessageAsync(tradingPartner, nameof(PlayerMessage.InventoryFull)).ConfigureAwait(false);
            return TradeResult.FailedByFullInventory;
        }

        var itemsDetached = false;
        try
        {
            itemContext.SetMoney(GetPersistentInventory(trader), (int)traderMoney);
            itemContext.SetMoney(GetPersistentInventory(tradingPartner), (int)partnerMoney);
            this.DetachItemsFromPersistenceContext(traderItems, trader.PersistenceContext);
            this.DetachItemsFromPersistenceContext(tradePartnerItems, trader.TradingPartner!.PersistenceContext);
            itemsDetached = true;
            if (!await itemContext.SaveChangesAsync().ConfigureAwait(false))
            {
                throw new InvalidOperationException("The trade transaction was not saved.");
            }
        }
        catch (Exception exception)
        {
            if (itemsDetached)
            {
                this.AttachItemsToPersistenceContext(traderItems, trader.PersistenceContext);
                this.AttachItemsToPersistenceContext(tradePartnerItems, tradingPartner.PersistenceContext);
            }

            await this.SendMessageAsync(trader, nameof(PlayerMessage.UnexpectedErrorDuringClosingTrade)).ConfigureAwait(false);
            await this.SendMessageAsync(tradingPartner, nameof(PlayerMessage.UnexpectedErrorDuringClosingTrade)).ConfigureAwait(false);
            context.Allowed = false;
            partnerContext.Allowed = false;
            (trader as Player)?.Logger.LogError(exception, $"An unexpected error occured during closing the trade. trader: {trader.Name}, partner:{tradingPartner.Name}");
            return TradeResult.Cancelled;
        }

        // Once committed, a view or plug-in failure must not turn the trade into a cancellation.
        this.AttachItemsToPersistenceContext(traderItems, tradingPartner.PersistenceContext);
        this.AttachItemsToPersistenceContext(tradePartnerItems, trader.PersistenceContext);
        trader.Money = (int)traderMoney;
        tradingPartner.Money = (int)partnerMoney;
        this.ResetTradeState(tradingPartner);
        this.ResetTradeState(trader);
        await tradingPartner.InvokeViewPlugInAsync<IChangeTradeButtonStatePlugIn>(p => p.ChangeTradeButtonStateAsync(TradeButtonState.Checked)).ConfigureAwait(false);
        this.CallPlugIn(traderItems, trader, tradingPartner);
        this.CallPlugIn(tradePartnerItems, tradingPartner, trader);
        FinishedTrades.Add(1);
        return TradeResult.Success;
    }

    private async ValueTask<bool> CheckItemsAreTradableAsync(ITrader trader, IEnumerable<Item> items)
    {
        var notTradable = items.FirstOrDefault(item => item.Definition is { IsTradable: false });
        if (notTradable is null)
        {
            return true;
        }

        (trader as Player)?.Logger.LogWarning(
            "Trader {0} offered {1}, which its item definition doesn't allow to trade. The trade is cancelled.",
            trader.Name,
            notTradable);
        await this.SendMessageAsync(trader, nameof(PlayerMessage.ItemCannotBeTraded)).ConfigureAwait(false);
        return false;
    }

    private void CallPlugIn(IEnumerable<Item> items, ITrader source, ITrader target)
    {
        var point = target.GameContext.PlugInManager.GetPlugInPoint<IItemTradedToOtherPlayerPlugIn>();
        if (point is null)
        {
            return;
        }

        foreach (var item in items)
        {
            point.ItemTraded(source, target, item);
        }
    }

    private void AttachItemsToPersistenceContext(IEnumerable<Item> items, IContext itemContext)
    {
        foreach (var item in items)
        {
            itemContext.Attach(item);
        }
    }

    private void DetachItemsFromPersistenceContext(IEnumerable<Item> items, IContext itemContext)
    {
        foreach (var item in items)
        {
            itemContext.Detach(item);
        }
    }
}
