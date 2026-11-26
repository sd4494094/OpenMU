// <copyright file="TradeSynchronization.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.Trade;

using System.Runtime.CompilerServices;
using Nito.AsyncEx;

/// <summary>
/// Serializes trade actions which affect either of the same traders.
/// </summary>
internal static class TradeSynchronization
{
    private static readonly ConditionalWeakTable<ITrader, AsyncLock> Locks = new();

    /// <summary>
    /// Acquires both trader locks in a stable order before any state transition starts.
    /// </summary>
    /// <param name="trader">The requesting trader.</param>
    /// <param name="partner">The partner observed before waiting.</param>
    /// <returns>The lock releases. The caller must recheck the observed partnership after acquiring them.</returns>
    internal static async ValueTask<IDisposable> LockAsync(ITrader trader, ITrader? partner)
    {
        var first = Locks.GetValue(trader, static _ => new AsyncLock());
        if (partner is null || ReferenceEquals(trader, partner))
        {
            return await first.LockAsync().ConfigureAwait(false);
        }

        var second = Locks.GetValue(partner, static _ => new AsyncLock());
        if (first.Id > second.Id)
        {
            (first, second) = (second, first);
        }

        var firstRelease = await first.LockAsync().ConfigureAwait(false);
        try
        {
            var secondRelease = await second.LockAsync().ConfigureAwait(false);
            return new LockReleaser(firstRelease, secondRelease);
        }
        catch
        {
            firstRelease.Dispose();
            throw;
        }
    }

    private sealed class LockReleaser(IDisposable first, IDisposable second) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                second.Dispose();
            }
            finally
            {
                first.Dispose();
            }
        }
    }
}
