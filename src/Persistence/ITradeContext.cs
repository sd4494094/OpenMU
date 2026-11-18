// <copyright file="ITradeContext.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;

/// <summary>
/// Persists transferred items and both inventory balances in one transaction.
/// </summary>
public interface ITradeContext : IContext
{
    /// <summary>
    /// Schedules an inventory balance for the same commit as the attached items.
    /// The live inventory is not changed before <see cref="IContext.SaveChangesAsync"/> succeeds.
    /// </summary>
    /// <param name="inventory">The inventory whose balance is to be saved.</param>
    /// <param name="money">The final balance, including outgoing and incoming offers.</param>
    void SetMoney(ItemStorage inventory, int money);

    /// <summary>
    /// Schedules an account coin balance for the same commit as the transferred items.
    /// The account's live balance remains unchanged until the commit succeeds.
    /// </summary>
    /// <param name="account">The account whose balance is to be saved.</param>
    /// <param name="coinType">The currency to update.</param>
    /// <param name="balance">The final balance.</param>
    void SetCashShopCoins(Account account, CashShopCoinType coinType, int balance);
}
