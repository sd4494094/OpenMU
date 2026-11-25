// <copyright file="LoginServer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.LoginServer;

using MUnique.OpenMU.Interfaces;
using Nito.AsyncEx;

/// <summary>
/// The login server which keeps track of connected accounts in-memory.
/// </summary>
public class LoginServer : ILoginServer, IMapTransferLoginServer
{
    private static readonly TimeSpan MapTransferLifetime = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, byte> _connectedAccounts = new();

    private readonly Dictionary<string, Reservation> _reservations = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<Guid, MapTransfer> _mapTransfers = new();

    private readonly Dictionary<string, Guid> _mapTransfersByAccount = new();

    private readonly AsyncLock _syncRoot = new();

    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="LoginServer"/> class.</summary>
    public LoginServer()
        : this(TimeProvider.System)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="LoginServer"/> class with an overridable clock.</summary>
    /// <param name="time">The clock used for administrative reservation expiry.</param>
    public LoginServer(TimeProvider time) => this._time = time;

    /// <inheritdoc />
    public async ValueTask<bool> TryReserveAccountAsync(string accountName, int reservationId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(reservationId, byte.MaxValue);
        using var gate = await this._syncRoot.LockAsync().ConfigureAwait(false);
        this.RemoveExpiredReservation(accountName);
        this.RemoveExpiredMapTransfer(accountName);
        if (this._connectedAccounts.ContainsKey(accountName)
            || this._mapTransfersByAccount.ContainsKey(accountName)
            || (this._reservations.TryGetValue(accountName, out var existing) && existing.Id != reservationId))
        {
            return false;
        }

        this._reservations[accountName] = new Reservation(reservationId, this._time.GetUtcNow().AddMinutes(5));
        return true;
    }

    /// <inheritdoc />
    public async ValueTask ReleaseAccountReservationAsync(string accountName, int reservationId)
    {
        using var gate = await this._syncRoot.LockAsync().ConfigureAwait(false);
        if (this._reservations.TryGetValue(accountName, out var existing) && existing.Id == reservationId)
        {
            this._reservations.Remove(accountName);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<Dictionary<string, byte>> GetSnapshotAsync()
    {
        using var l = await this._syncRoot.LockAsync();
        return new(this._connectedAccounts);
    }

    /// <inheritdoc/>
    public async Task<bool> TryLoginAsync(string accountName, byte serverId)
    {
        using var l = await this._syncRoot.LockAsync();
        this.RemoveExpiredReservation(accountName);
        this.RemoveExpiredMapTransfer(accountName);
        if (this._connectedAccounts.ContainsKey(accountName)
            || this._mapTransfersByAccount.ContainsKey(accountName)
            || this._reservations.ContainsKey(accountName))
        {
            return false;
        }

        this._connectedAccounts.Add(accountName, serverId);
        return true;
    }

    /// <summary>Replaces the source login with a short-lived map-transfer reservation.</summary>
    /// <param name="accountName">The canonical account name of the source registration.</param>
    /// <param name="characterName">The selected character whose state was saved by the source server.</param>
    /// <param name="sourceServerId">The source game server.</param>
    /// <param name="targetServerId">The game server authorized to claim the credential.</param>
    /// <returns>The single-use credential, or <see langword="null"/> if the source does not own the account.</returns>
    /// <remarks>
    /// The caller must detach the source character from gameplay and confirm its last save before replacing the registration.
    /// A successful call releases the source's registration without making the account available for an ordinary login.
    /// </remarks>
    public async ValueTask<Guid?> BeginMapTransferAsync(string accountName, string characterName, byte sourceServerId, byte targetServerId)
    {
        if (sourceServerId == targetServerId || string.IsNullOrWhiteSpace(characterName))
        {
            return null;
        }

        using var gate = await this._syncRoot.LockAsync().ConfigureAwait(false);
        this.RemoveExpiredMapTransfers();
        if (!this._connectedAccounts.TryGetValue(accountName, out var currentServerId)
            || currentServerId != sourceServerId)
        {
            return null;
        }

        var credential = Guid.NewGuid();
        this._mapTransfers.Add(credential, new(accountName, characterName, sourceServerId, targetServerId, this._time.GetUtcNow().Add(MapTransferLifetime)));
        this._mapTransfersByAccount.Add(accountName, credential);
        this._connectedAccounts.Remove(accountName);
        return credential;
    }

    /// <summary>Consumes a map-transfer reservation and establishes the new account registration.</summary>
    /// <param name="credential">The credential presented on the target connection.</param>
    /// <param name="characterName">The exact character name presented on the target connection.</param>
    /// <param name="targetServerId">The receiving game server.</param>
    /// <returns>The canonical account name for the target session, or <see langword="null"/> if the claim is refused.</returns>
    public async ValueTask<string?> ClaimMapTransferAsync(Guid credential, string characterName, byte targetServerId)
    {
        using var gate = await this._syncRoot.LockAsync().ConfigureAwait(false);
        if (!this._mapTransfers.TryGetValue(credential, out var transfer))
        {
            return null;
        }

        if (transfer.ExpiresAt <= this._time.GetUtcNow())
        {
            this.RemoveMapTransfer(credential, transfer);
            return null;
        }

        if (transfer.TargetServerId != targetServerId
            || !string.Equals(transfer.CharacterName, characterName, StringComparison.Ordinal))
        {
            return null;
        }

        // A normal login cannot claim this account between credential consumption and target registration.
        this._connectedAccounts.Add(transfer.AccountName, targetServerId);
        this.RemoveMapTransfer(credential, transfer);
        return transfer.AccountName;
    }

    /// <summary>Cancels an unclaimed transfer and makes the account available to log in again.</summary>
    /// <param name="credential">The credential issued to the source game server.</param>
    /// <param name="sourceServerId">The server which issued the transfer.</param>
    /// <returns>Whether the transfer was canceled.</returns>
    public async ValueTask<bool> CancelMapTransferAsync(Guid credential, byte sourceServerId)
    {
        using var gate = await this._syncRoot.LockAsync().ConfigureAwait(false);
        if (!this._mapTransfers.TryGetValue(credential, out var transfer) || transfer.SourceServerId != sourceServerId)
        {
            return false;
        }

        this.RemoveMapTransfer(credential, transfer);
        return true;
    }

    /// <inheritdoc/>
    public async ValueTask LogOffAsync(string accountName, byte serverId)
    {
        using var l = await this._syncRoot.LockAsync();

        // A late log off of another server must not remove the current login of the account.
        if (this._connectedAccounts.TryGetValue(accountName, out var currentServerId) && currentServerId == serverId)
        {
            this._connectedAccounts.Remove(accountName);
        }
    }

    private void RemoveExpiredReservation(string accountName)
    {
        if (this._reservations.TryGetValue(accountName, out var reservation) && reservation.ExpiresAt <= this._time.GetUtcNow())
        {
            this._reservations.Remove(accountName);
        }
    }

    private void RemoveExpiredMapTransfer(string accountName)
    {
        if (this._mapTransfersByAccount.TryGetValue(accountName, out var credential) &&
            this._mapTransfers[credential] is { } transfer && transfer.ExpiresAt <= this._time.GetUtcNow())
        {
            this.RemoveMapTransfer(credential, transfer);
        }
    }

    private void RemoveExpiredMapTransfers()
    {
        var now = this._time.GetUtcNow();
        foreach (var (credential, transfer) in this._mapTransfers.Where(pair => pair.Value.ExpiresAt <= now).ToArray())
        {
            this.RemoveMapTransfer(credential, transfer);
        }
    }

    private void RemoveMapTransfer(Guid credential, MapTransfer transfer)
    {
        this._mapTransfers.Remove(credential);
        this._mapTransfersByAccount.Remove(transfer.AccountName);
    }

    private sealed record Reservation(int Id, DateTimeOffset ExpiresAt);

    private sealed record MapTransfer(string AccountName, string CharacterName, byte SourceServerId, byte TargetServerId, DateTimeOffset ExpiresAt);
}
