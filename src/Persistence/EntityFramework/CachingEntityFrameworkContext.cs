// <copyright file="CachingEntityFrameworkContext.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.EntityFramework;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.Interfaces;

/// <summary>
/// Implementation of <see cref="IContext"/> for the entity framework <see cref="PersistenceContextProvider"/>.
/// </summary>
/// <remarks>
/// TODO: Check if this class can be removed. It doesn't seem to have any additional logic to <see cref="EntityFrameworkContext"/>.
/// </remarks>
internal class CachingEntityFrameworkContext : EntityFrameworkContextBase, ITradeContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CachingEntityFrameworkContext" /> class.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <param name="repositoryProvider">The repository provider.</param>
    /// <param name="changeListener">The change listener.</param>
    /// <param name="logger">The logger.</param>
    public CachingEntityFrameworkContext(DbContext context, IContextAwareRepositoryProvider repositoryProvider, IConfigurationChangeListener? changeListener, ILogger<CachingEntityFrameworkContext> logger)
        : base(context, repositoryProvider, true, changeListener, logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CachingEntityFrameworkContext" /> class.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <param name="repositoryProvider">The repository provider.</param>
    /// <param name="isOwner">if set to <c>true</c> this instance owns the <paramref name="context" />.</param>
    /// <param name="changeListener">The change listener.</param>
    /// <param name="logger">The logger.</param>
    public CachingEntityFrameworkContext(DbContext context, IContextAwareRepositoryProvider repositoryProvider, bool isOwner, IConfigurationChangeListener? changeListener, ILogger<CachingEntityFrameworkContext> logger)
        : base(context, repositoryProvider, isOwner, changeListener, logger)
    {
    }

    /// <inheritdoc />
    public void SetMoney(DataModel.Entities.ItemStorage inventory, int money)
    {
        // Track only the balance, without attaching the player's live object graph or
        // changing its reserved money before the transaction commits. An explicit
        // modified flag also persists a debit which was already reserved in memory.
        var id = inventory.GetId();
        if (id == Guid.Empty)
        {
            throw new ArgumentException("The trade inventory must have a persisted identity.", nameof(inventory));
        }

        var storage = new Model.ItemStorage { Id = id, Money = money };
        this.Attach(storage);
        this.Context.Entry(storage).Property(s => s.Money).IsModified = true;
    }

    /// <inheritdoc />
    public void SetCashShopCoins(DataModel.Entities.Account account, DataModel.Configuration.CashShopCoinType coinType, int balance)
    {
        var id = account.GetId();
        if (id == Guid.Empty)
        {
            throw new ArgumentException("The shop account must have a persisted identity.", nameof(account));
        }

        var property = coinType switch
        {
            DataModel.Configuration.CashShopCoinType.WCoinC => nameof(Model.Account.WCoinC),
            DataModel.Configuration.CashShopCoinType.WCoinP => nameof(Model.Account.WCoinP),
            DataModel.Configuration.CashShopCoinType.GoblinPoints => nameof(Model.Account.GoblinPoints),
            _ => throw new ArgumentOutOfRangeException(nameof(coinType), coinType, null),
        };
        var storedAccount = this.Context.Set<Model.Account>().Local.FirstOrDefault(a => a.Id == id);
        if (storedAccount is null)
        {
            storedAccount = new Model.Account { Id = id };
            this.Attach(storedAccount);
        }

        // Attach only an identity stub; saving the live account graph here would also
        // save unrelated characters, inventories and pending grants.
        var entry = this.Context.Entry(storedAccount).Property<int>(property);
        entry.CurrentValue = balance;
        entry.IsModified = true;
    }
}
