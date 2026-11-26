// <copyright file="TradeContext.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.EntityFramework;

using Microsoft.EntityFrameworkCore;
using MUnique.OpenMU.Persistence.EntityFramework.Model;

/// <summary>
/// A context which is used for trades.
/// </summary>
/// <remarks>
/// Tracks item ownership, slots and inventory balances, so a completed trade has one commit boundary.
/// </remarks>
/// <seealso cref="Microsoft.EntityFrameworkCore.DbContext" />
internal class TradeContext : AccountContext
{
    /// <inheritdoc/>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        this.Configure(optionsBuilder);
    }

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Balances participate in the trade transaction, but unrelated inventory items
        // must not be attached or saved through their collection navigation.
        modelBuilder.Entity<ItemStorage>().Ignore(storage => storage.RawItems);
    }
}
