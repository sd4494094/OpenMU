// <copyright file="ZenShopCurrencyExchange.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerShopCurrencies;

using MUnique.OpenMU.GameLogic.Views.Inventory;

/// <summary>
/// Pays prices in zen, by transferring the zen of the inventory of the buyer to the one of the seller.
/// </summary>
public sealed class ZenShopCurrencyExchange : IShopCurrencyExchange
{
    /// <inheritdoc />
    public ShopPaymentResult CheckFunds(Player buyer, ShopPrice price)
    {
        return buyer.Money < price.Amount ? ShopPaymentResult.LackOfCurrency : ShopPaymentResult.Success;
    }

    /// <inheritdoc />
    public ValueTask<ShopPaymentResult> TryPayAsync(ShopPurchase purchase)
    {
        if (purchase.Buyer.SelectedCharacter?.Inventory is null || purchase.Seller.SelectedCharacter?.Inventory is null)
        {
            return ValueTask.FromResult(ShopPaymentResult.Failed);
        }

        if (purchase.Buyer.Money < purchase.Price.Amount)
        {
            return ValueTask.FromResult(ShopPaymentResult.LackOfCurrency);
        }

        var sellerBalance = (long)purchase.Seller.Money + purchase.Price.Amount;
        if (sellerBalance > purchase.Seller.GameContext.Configuration.MaximumInventoryMoney || sellerBalance > int.MaxValue)
        {
            return ValueTask.FromResult(ShopPaymentResult.SellerOverflow);
        }

        purchase.SetMoney(purchase.Buyer.Money - purchase.Price.Amount, (int)sellerBalance);
        return ValueTask.FromResult(ShopPaymentResult.Success);
    }

    /// <inheritdoc />
    public ValueTask NotifySellerAsync(ShopPurchase purchase)
    {
        return purchase.Seller.InvokeViewPlugInAsync<IUpdateMoneyPlugIn>(p => p.UpdateMoneyAsync());
    }

    /// <inheritdoc />
    public ValueTask NotifyBuyerAsync(ShopPurchase purchase)
    {
        return purchase.Buyer.InvokeViewPlugInAsync<IUpdateMoneyPlugIn>(p => p.UpdateMoneyAsync());
    }
}
