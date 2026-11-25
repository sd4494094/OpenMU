// <copyright file="PlayerPersistence.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic;

using System.Threading;
using MUnique.OpenMU.Persistence;
using Nito.AsyncEx;

/// <summary>
/// Serializes the context mutations of a <see cref="Player"/> against its progress saves.
/// </summary>
/// <remarks>
/// The periodic progress save (<see cref="PlugIns.PeriodicSaveProgressPlugIn"/>) runs on an
/// independent timer flow. Action handlers mutate tracked entities with plain field/collection
/// writes (e.g. crafting toggling <c>item.ItemOptions</c>) which bypass the persistence context's
/// own lock; if such a mutation runs while <see cref="IContext.SaveChangesAsync"/> enumerates the
/// change tracker, the save throws (collection-modified / DbUpdateConcurrency) and every following
/// save fails too, so the whole session is lost on relog. Serializing the packet handler funnel
/// and the save against each other closes that window. The lock is re-entrant per asynchronous
/// flow, so an inline save inside an already-serialized handler does not deadlock.
/// <para>
/// Invariant: never acquire another player's persistence lock (via their
/// <see cref="Player.SaveProgressAsync"/> or <see cref="RunExclusiveAsync{T}"/>) from inside a
/// packet handler, which already holds this player's lock, unless a global lock order is enforced.
/// Trade acceptance acquires both persistence locks in their stable ID order before taking
/// either trade lock. Acquiring the partner's persistence lock while already holding a trade
/// lock would deadlock with a partner packet holding its persistence lock and waiting for the trade.
/// Cross-player operations must enter through the paired overload before holding either lock.
/// </para>
/// </remarks>
internal sealed class PlayerPersistence
{
    private readonly Player _player;

    private readonly AsyncLock _lock = new();

    /// <summary>
    /// Tracks, per asynchronous flow, whether <see cref="_lock"/> is already held, so the
    /// lock can be re-entered (Nito's <see cref="AsyncLock"/> is not reentrant). It is an instance
    /// field on purpose: reentrancy must be tracked per player, so a flow holding player A's lock
    /// still acquires player B's lock (e.g. during a trade) instead of wrongly skipping it.
    /// </summary>
    private readonly AsyncLocal<bool> _lockHeld = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayerPersistence"/> class.
    /// </summary>
    /// <param name="player">The player.</param>
    public PlayerPersistence(Player player)
    {
        this._player = player;
    }

    /// <summary>Gets or sets whether the target game server has exclusive authority to save the session.</summary>
    public bool WritesSuppressed { get; set; }

    /// <summary>
    /// Saves the progress of the player.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Success of the save operation.</returns>
    public async ValueTask<bool> SaveProgressAsync(CancellationToken cancellationToken = default)
    {
        if (this._player.IsTemplatePlayer)
        {
            return true;
        }

        return await this.RunExclusiveAsync(
            () => this.WritesSuppressed ? ValueTask.FromResult(true) : this.SaveChangesAsync(cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the given operation while holding this player's persistence lock, so that context
    /// mutations and progress saves for the player never run concurrently.
    /// </summary>
    /// <typeparam name="T">The result type of the operation.</typeparam>
    /// <param name="operation">The operation to run exclusively.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result of the operation.</returns>
    public async ValueTask<T> RunExclusiveAsync<T>(Func<ValueTask<T>> operation, CancellationToken cancellationToken = default)
    {
        if (this._lockHeld.Value)
        {
            return await operation().ConfigureAwait(false);
        }

        using var l = await this._lock.LockAsync(cancellationToken).ConfigureAwait(false);
        this._lockHeld.Value = true;
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            this._lockHeld.Value = false;
        }
    }

    /// <summary>
    /// Runs the given operation while holding this player's persistence lock.
    /// </summary>
    /// <param name="operation">The operation to run exclusively.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A value task which completes when the operation completed.</returns>
    public async ValueTask RunExclusiveAsync(Func<ValueTask> operation, CancellationToken cancellationToken = default)
    {
        if (this._lockHeld.Value)
        {
            await operation().ConfigureAwait(false);
            return;
        }

        using var l = await this._lock.LockAsync(cancellationToken).ConfigureAwait(false);
        this._lockHeld.Value = true;
        try
        {
            await operation().ConfigureAwait(false);
        }
        finally
        {
            this._lockHeld.Value = false;
        }
    }

    /// <summary>
    /// Runs an operation with two players' persistence locks in a stable order.
    /// </summary>
    /// <param name="other">The other player's persistence coordinator.</param>
    /// <param name="operation">The operation to run exclusively.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The task.</returns>
    /// <exception cref="InvalidOperationException">Only one of the two locks is already held by this flow.</exception>
    public ValueTask RunExclusiveAsync(PlayerPersistence other, Func<ValueTask> operation, CancellationToken cancellationToken = default)
    {
        if (ReferenceEquals(this, other) || (this._lockHeld.Value && other._lockHeld.Value))
        {
            return this.RunExclusiveAsync(operation, cancellationToken);
        }

        if (this._lockHeld.Value || other._lockHeld.Value)
        {
            throw new InvalidOperationException("Acquire both persistence locks before entering a single-player persistence scope.");
        }

        var first = this._lock.Id < other._lock.Id ? this : other;
        var second = ReferenceEquals(first, this) ? other : this;
        return first.RunExclusiveAsync(() => second.RunExclusiveAsync(operation, cancellationToken), cancellationToken);
    }

    private async ValueTask<bool> SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (this._player.PlayerState.CurrentState != PlayerState.NpcDialogOpened
            || this._player.OpenedNpc?.Definition.ItemCraftings.Any() != true
            || this._player.SelectedCharacter is not { } character
            || this._player.TemporaryStorage is not { } temporary
            || !temporary.Items.Any())
        {
            return await this._player.PersistenceContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        // The machine's storage is deliberately transient. During a save, give its current
        // items a persistent character owner, in the same commit as payment and consumed
        // ingredients. A new session can then recover them even if the process stops before
        // the dialog closes. Trade reservations must never enter this path.
        var recovery = character.RecoveryStorage ??= this._player.PersistenceContext.CreateNew<ItemStorage>();
        var staged = temporary.Items.Where(item => !recovery.Items.Contains(item)).ToList();
        foreach (var item in staged)
        {
            recovery.Items.Add(item);
        }

        try
        {
            return await this._player.PersistenceContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Keep the live dialog authoritative. Leaving these references here would let
            // normal recovery return a second copy after the item is moved or consumed.
            // Every later save stages the current dialog contents again under this gate.
            foreach (var item in staged)
            {
                recovery.Items.Remove(item);
            }
        }
    }
}
