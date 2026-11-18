// <copyright file="ShopPurchase.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerShopCurrencies;

using MUnique.OpenMU.Persistence;

/// <summary>
/// The purchase of an item from a player shop, for which the price is paid.
/// </summary>
/// <param name="Buyer">The buyer.</param>
/// <param name="Seller">The seller, which owns the shop.</param>
/// <param name="Price">The price.</param>
/// <param name="TradeContext">
/// The persistence context in which the sold item is moved from the seller to the buyer.
/// Items which are paid with are moved in it, too, so that everything is saved together.
/// </param>
public sealed record ShopPurchase(Player Buyer, Player Seller, ShopPrice Price, ITradeContext TradeContext)
{
    /// <summary>
    /// Gets the items which the buyer paid with, together with their previous slots in the inventory of the buyer.
    /// They are already moved into the inventory of the seller and attached to the <see cref="TradeContext"/>.
    /// </summary>
    public IList<(Item Item, byte BuyerSlot)> PaymentItems { get; } = new List<(Item Item, byte BuyerSlot)>();

    private Action? CompletePaymentAction { get; set; }

    /// <summary>
    /// Schedules both inventory balances without publishing an uncommitted payment.
    /// </summary>
    /// <param name="buyerBalance">The buyer's final balance.</param>
    /// <param name="sellerBalance">The seller's final balance.</param>
    internal void SetMoney(int buyerBalance, int sellerBalance)
    {
        var buyerInventory = this.Buyer.SelectedCharacter!.Inventory!;
        var sellerInventory = this.Seller.SelectedCharacter!.Inventory!;
        this.TradeContext.SetMoney(buyerInventory, buyerBalance);
        this.TradeContext.SetMoney(sellerInventory, sellerBalance);
        this.CompletePaymentAction = () =>
        {
            buyerInventory.Money = buyerBalance;
            sellerInventory.Money = sellerBalance;
        };
    }

    /// <summary>
    /// Schedules both account balances in the item transaction.
    /// </summary>
    /// <param name="coinType">The currency.</param>
    /// <param name="buyerBalance">The buyer's final balance.</param>
    /// <param name="sellerBalance">The seller's final balance.</param>
    internal void SetCoins(CashShopCoinType coinType, int buyerBalance, int sellerBalance)
    {
        var buyerAccount = this.Buyer.Account!;
        var sellerAccount = this.Seller.Account!;
        this.TradeContext.SetCashShopCoins(buyerAccount, coinType, buyerBalance);
        this.TradeContext.SetCashShopCoins(sellerAccount, coinType, sellerBalance);
        this.CompletePaymentAction = () =>
        {
            CashShop.CashShopCoinExtensions.SetCashShopCoins(buyerAccount, coinType, buyerBalance);
            CashShop.CashShopCoinExtensions.SetCashShopCoins(sellerAccount, coinType, sellerBalance);
        };
    }

    /// <summary>
    /// Publishes the balances only after persistence has accepted the purchase.
    /// </summary>
    internal void CompletePayment() => this.CompletePaymentAction?.Invoke();
}
