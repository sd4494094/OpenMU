// <copyright file="AccountCoinShopCurrencyExchange.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerShopCurrencies;

using MUnique.OpenMU.GameLogic.CashShop;

/// <summary>
/// Pays prices in account coins (WCoin C, WCoin P, Goblin Points), by transferring them from the account
/// of the buyer to the one of the seller.
/// </summary>
/// <remarks>
/// Pending grants are saved before the purchase. The payment itself schedules both
/// account balances in the same transaction as the sold item. The caller holds both
/// player persistence locks before entering the store lock.
/// </remarks>
public sealed class AccountCoinShopCurrencyExchange : IShopCurrencyExchange
{
    /// <inheritdoc />
    public ShopPaymentResult CheckFunds(Player buyer, ShopPrice price)
    {
        if (price.CoinType is not { } coinType || buyer.Account is not { } account)
        {
            return ShopPaymentResult.Failed;
        }

        return account.GetCashShopCoins(coinType) < price.Amount ? ShopPaymentResult.LackOfCurrency : ShopPaymentResult.Success;
    }

    /// <inheritdoc />
    public async ValueTask<ShopPaymentResult> TryPayAsync(ShopPurchase purchase)
    {
        if (purchase.Price.CoinType is not { } coinType
            || purchase.Buyer.Account is not { } buyerAccount
            || purchase.Seller.Account is not { } sellerAccount)
        {
            return ShopPaymentResult.Failed;
        }

        await purchase.Buyer.ApplyPendingCashShopCoinGrantsAsync().ConfigureAwait(false);
        await purchase.Seller.ApplyPendingCashShopCoinGrantsAsync().ConfigureAwait(false);
        if (!await purchase.Buyer.SaveProgressAsync().ConfigureAwait(false)
            || !await purchase.Seller.SaveProgressAsync().ConfigureAwait(false))
        {
            return ShopPaymentResult.Failed;
        }

        var buyerBalance = buyerAccount.GetCashShopCoins(coinType);
        if (buyerBalance < purchase.Price.Amount)
        {
            return ShopPaymentResult.LackOfCurrency;
        }

        var sellerBalance = sellerAccount.GetCashShopCoins(coinType);
        if ((long)sellerBalance + purchase.Price.Amount > int.MaxValue)
        {
            return ShopPaymentResult.SellerOverflow;
        }

        purchase.SetCoins(coinType, buyerBalance - purchase.Price.Amount, sellerBalance + purchase.Price.Amount);
        return ShopPaymentResult.Success;
    }

    /// <inheritdoc />
    public ValueTask NotifySellerAsync(ShopPurchase purchase)
    {
        return purchase.Seller.ShowLocalizedBlueMessageAsync(
            nameof(PlayerMessage.PlayerShopCoinsReceived),
            purchase.Price.Amount,
            purchase.Seller.GetCurrencyName(purchase.Price));
    }

    /// <inheritdoc />
    public ValueTask NotifyBuyerAsync(ShopPurchase purchase)
    {
        return purchase.Buyer.ShowLocalizedBlueMessageAsync(
            nameof(PlayerMessage.PlayerShopCoinsPaid),
            purchase.Price.Amount,
            purchase.Buyer.GetCurrencyName(purchase.Price));
    }
}
