// <copyright file="PersistentLoginServer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.CentralServer.Host;

using System.Text.Json;
using global::Dapr.Client;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.Interfaces;
using Nito.AsyncEx;

/// <summary>
/// An implementation of a <see cref="ILoginServer"/> which persists the login state in a dapr state store.
/// </summary>
public sealed class PersistentLoginServer : ILoginServer
{
    private const string StoreName = "login-state";

    private const int OfflineServerId = -1;

    private static readonly TimeSpan MapTransferLifetime = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions StateJson = new(JsonSerializerDefaults.Web);

    // Applies only to reservations. Ordinary offline/login state must remain durable.
    private static readonly IReadOnlyDictionary<string, string> ReservationMetadata = new Dictionary<string, string> { ["ttlInSeconds"] = "300" };
    private static readonly IReadOnlyDictionary<string, string> DurableMetadata = new Dictionary<string, string> { ["ttlInSeconds"] = "-1" };
    private static readonly IReadOnlyDictionary<string, string> TransferLookupMetadata = new Dictionary<string, string> { ["ttlInSeconds"] = "30" };

    private readonly ILogger<PersistentLoginServer> _logger;

    private readonly DaprClient _daprClient;

    // A destination must be indexed before claiming ownership. Serialize that step with local
    // server cleanup and logoff so neither can remove the index entry between these two writes.
    private readonly AsyncLock _indexLock = new();

    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="PersistentLoginServer"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="daprClient">The dapr client.</param>
    public PersistentLoginServer(ILogger<PersistentLoginServer> logger, DaprClient daprClient)
        : this(logger, daprClient, TimeProvider.System)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PersistentLoginServer"/> class with an overridable clock.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="daprClient">The Dapr client.</param>
    /// <param name="time">The clock used to expire transfer reservations.</param>
    public PersistentLoginServer(ILogger<PersistentLoginServer> logger, DaprClient daprClient, TimeProvider time)
    {
        this._logger = logger;
        this._daprClient = daprClient;
        this._time = time;
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryReserveAccountAsync(string accountName, int reservationId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(reservationId, byte.MaxValue);
        var (state, etag) = await this.GetAccountStateAsync(accountName).ConfigureAwait(false);
        if (GetRegistration(state) == reservationId)
        {
            return true;
        }

        if (this.IsAccountOccupied(state))
        {
            return false;
        }

        // Accounts edited through the admin layer must have an initialized state entry.
        // Failing closed avoids an unconditional first insert racing a real login.
        if (string.IsNullOrEmpty(etag))
        {
            return false;
        }

        return await this._daprClient.TrySaveStateAsync(StoreName, accountName, reservationId, etag, metadata: ReservationMetadata).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ReleaseAccountReservationAsync(string accountName, int reservationId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(reservationId, byte.MaxValue);
        var (state, etag) = await this.GetAccountStateAsync(accountName).ConfigureAwait(false);
        if (GetRegistration(state) == reservationId && !await this._daprClient.TrySaveStateAsync(StoreName, accountName, OfflineServerId, etag, metadata: DurableMetadata).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The offline account reservation could not be released.");
        }
    }

    /// <summary>
    /// Removes the server.
    /// </summary>
    /// <param name="serverId">The server identifier.</param>
    public async Task RemoveServerAsync(byte serverId)
    {
        using var guard = await this._indexLock.LockAsync().ConfigureAwait(false);
        var indexName = GetIndexName(serverId);
        while (true)
        {
            var (serverIndex, eTag) = await this._daprClient.GetStateAndETagAsync<HashSet<string>>(StoreName, indexName, ConsistencyMode.Strong).ConfigureAwait(false);
            if (serverIndex is null || serverIndex.Count == 0)
            {
                return;
            }

            foreach (var accountName in serverIndex)
            {
                await this.SetAccountOfflineAsync(accountName, serverId).ConfigureAwait(false);
            }

            serverIndex.Clear();
            if (await this._daprClient.TrySaveStateAsync(StoreName, indexName, serverIndex, eTag).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryLoginAsync(string accountName, byte serverId)
    {
        try
        {
            var (state, eTag) = await this.GetAccountStateAsync(accountName).ConfigureAwait(false);
            if (this.IsAccountOccupied(state))
            {
                return false;
            }

            if (string.IsNullOrEmpty(eTag))
            {
                // Never logged in, so first insert a fresh state and try again.
                // We never want to have the same account logged in twice, because that may lead to game mechanic exploits.
                await this._daprClient.SaveStateAsync<int?>(StoreName, accountName, OfflineServerId, new StateOptions { Concurrency = ConcurrencyMode.FirstWrite, Consistency = ConsistencyMode.Strong }, metadata: DurableMetadata).ConfigureAwait(false);
                return await this.TryLoginAsync(accountName, serverId).ConfigureAwait(false);
            }

            if (!await this._daprClient.TrySaveStateAsync(StoreName, accountName, serverId, eTag, metadata: DurableMetadata).ConfigureAwait(false))
            {
                // Another server changed the state of the account in the meantime.
                return false;
            }

            using var guard = await this._indexLock.LockAsync().ConfigureAwait(false);
            await this.AddToIndexAsync(accountName, serverId).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Couldn't get/set logged-in state for account {0}", accountName);
            return false;
        }
    }

    /// <inheritdoc />
    public async ValueTask LogOffAsync(string accountName, byte serverId)
    {
        try
        {
            using var guard = await this._indexLock.LockAsync().ConfigureAwait(false);
            await this.SetAccountOfflineAsync(accountName, serverId).ConfigureAwait(false);
            await this.RemoveFromIndexAsync(accountName, serverId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Unexpected error when removing account {0} from server {1}", accountName, serverId);
        }
    }

    /// <inheritdoc />
    public async ValueTask<Dictionary<string, byte>> GetSnapshotAsync()
    {
        var result = new Dictionary<string, byte>();
        var indexNames = Enumerable.Range(byte.MinValue, byte.MaxValue + 1).Select(GetIndexName).ToList();
        var serverIndexes = await this._daprClient.GetBulkStateAsync<HashSet<string>>(StoreName, indexNames, parallelism: null).ConfigureAwait(false);
        var indexedAccounts = serverIndexes.Where(index => index.Value is not null)
            .SelectMany(index => index.Value).Distinct(StringComparer.Ordinal).ToList();
        if (indexedAccounts.Count == 0)
        {
            return result;
        }

        // Indexes can contain stale entries if a transfer was interrupted between writes.
        // Only the canonical account key establishes an active server registration.
        var states = await this._daprClient.GetBulkStateAsync<JsonElement>(StoreName, indexedAccounts, parallelism: null).ConfigureAwait(false);
        foreach (var state in states)
        {
            if (GetRegistration(state.Value) is >= byte.MinValue and <= byte.MaxValue and { } serverId)
            {
                result[state.Key] = (byte)serverId;
            }
        }

        return result;
    }

    /// <summary>Reserves a saved, detached session for the target game server.</summary>
    /// <param name="accountName">The canonical account name of the source registration.</param>
    /// <param name="characterName">The character that the target must select.</param>
    /// <param name="sourceServerId">The current owner of the registration.</param>
    /// <param name="targetServerId">The game server allowed to claim the reservation.</param>
    /// <returns>A single-use credential, or null when the source does not own the account.</returns>
    public async ValueTask<Guid?> BeginMapTransferAsync(string accountName, string characterName, byte sourceServerId, byte targetServerId)
    {
        if (sourceServerId == targetServerId || string.IsNullOrWhiteSpace(characterName))
        {
            return null;
        }

        var (state, eTag) = await this.GetAccountStateAsync(accountName).ConfigureAwait(false);
        if (GetRegistration(state) != sourceServerId || string.IsNullOrEmpty(eTag))
        {
            return null;
        }

        var credential = Guid.NewGuid();
        var reservation = new MapTransferReservation(credential, characterName, sourceServerId, targetServerId, this._time.GetUtcNow().Add(MapTransferLifetime));

        // A leftover lookup is harmless. Only a matching, unexpired account record is claimable.
        await this._daprClient.SaveStateAsync(StoreName, GetTransferLookupKey(credential), accountName, metadata: TransferLookupMetadata).ConfigureAwait(false);

        // Keep the account key even after expiry so that its ETag is not reset by a store TTL.
        return await this._daprClient.TrySaveStateAsync(StoreName, accountName, reservation, eTag, metadata: DurableMetadata).ConfigureAwait(false) ? credential : null;
    }

    /// <summary>Consumes a valid reservation and registers the account on the receiving game server.</summary>
    /// <param name="credential">The single-use credential presented by the client.</param>
    /// <param name="characterName">The character from the client's transfer packet.</param>
    /// <param name="targetServerId">The receiving game server.</param>
    /// <returns>The canonical account name, or null when the claim is refused.</returns>
    public async ValueTask<string?> ClaimMapTransferAsync(Guid credential, string characterName, byte targetServerId)
    {
        if (await this.GetTransferAccountAsync(credential).ConfigureAwait(false) is not { } accountName)
        {
            return null;
        }

        using var guard = await this._indexLock.LockAsync().ConfigureAwait(false);
        var (state, eTag) = await this.GetAccountStateAsync(accountName).ConfigureAwait(false);
        if (GetMapTransfer(state) is not { } reservation
            || reservation.Credential != credential
            || reservation.TargetServerId != targetServerId
            || !string.Equals(reservation.CharacterName, characterName, StringComparison.Ordinal)
            || reservation.ExpiresAt <= this._time.GetUtcNow()
            || string.IsNullOrEmpty(eTag))
        {
            return null;
        }

        await this.AddToIndexAsync(accountName, targetServerId).ConfigureAwait(false);
        if (reservation.ExpiresAt <= this._time.GetUtcNow())
        {
            return null;
        }

        if (!await this._daprClient.TrySaveStateAsync(StoreName, accountName, targetServerId, eTag, metadata: DurableMetadata).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            await this.RemoveFromIndexAsync(accountName, reservation.SourceServerId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Ownership committed. The source index can be ignored by snapshots and cleaned up later.
            this._logger.LogWarning(ex, "Couldn't remove the source index entry after transferring account {AccountName}", accountName);
        }

        return accountName;
    }

    /// <summary>Releases an unclaimed transfer reservation.</summary>
    /// <param name="credential">The credential previously issued to the source server.</param>
    /// <param name="sourceServerId">The source requesting cancellation.</param>
    /// <returns>Whether the reservation was canceled.</returns>
    public async ValueTask<bool> CancelMapTransferAsync(Guid credential, byte sourceServerId)
    {
        if (await this.GetTransferAccountAsync(credential).ConfigureAwait(false) is not { } accountName)
        {
            return false;
        }

        var (state, eTag) = await this.GetAccountStateAsync(accountName).ConfigureAwait(false);
        return GetMapTransfer(state) is { } reservation
            && reservation.Credential == credential && reservation.SourceServerId == sourceServerId
            && !string.IsNullOrEmpty(eTag)
            && await this._daprClient.TrySaveStateAsync(StoreName, accountName, OfflineServerId, eTag, metadata: DurableMetadata).ConfigureAwait(false);
    }

    private static int? GetRegistration(JsonElement state) => state.ValueKind == JsonValueKind.Number ? state.GetInt32() : null;

    private static MapTransferReservation? GetMapTransfer(JsonElement state)
        => state.ValueKind == JsonValueKind.Object ? state.Deserialize<MapTransferReservation>(StateJson) : null;

    private static string GetTransferLookupKey(Guid credential) => $"map-transfer/{credential:N}";

    private static string GetIndexName(int serverId) => $"serverindex-{serverId}";

    private Task<(JsonElement State, string ETag)> GetAccountStateAsync(string accountName)
        => this._daprClient.GetStateAndETagAsync<JsonElement>(StoreName, accountName, ConsistencyMode.Strong);

    private Task<string?> GetTransferAccountAsync(Guid credential)
        => this._daprClient.GetStateAsync<string?>(StoreName, GetTransferLookupKey(credential), ConsistencyMode.Strong);

    private bool IsAccountOccupied(JsonElement state) => state.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => false,
        JsonValueKind.Number => GetRegistration(state) != OfflineServerId,
        JsonValueKind.Object => GetMapTransfer(state) is not { } reservation || reservation.ExpiresAt > this._time.GetUtcNow(),
        _ => true,
    };

    private async Task AddToIndexAsync(string accountName, byte serverId)
    {
        var indexName = GetIndexName(serverId);
        var (serverIndex, eTag) = await this._daprClient.GetStateAndETagAsync<HashSet<string>>(StoreName, indexName, ConsistencyMode.Strong).ConfigureAwait(false);
        if (serverIndex is null)
        {
            serverIndex = new HashSet<string>();
            serverIndex.Add(accountName);
            await this._daprClient.SaveStateAsync(StoreName, indexName, serverIndex).ConfigureAwait(false);
            return;
        }

        if (serverIndex.Add(accountName))
        {
            if (!await this._daprClient.TrySaveStateAsync(StoreName, indexName, serverIndex, eTag).ConfigureAwait(false))
            {
                // try again, if it failed
                await this.AddToIndexAsync(accountName, serverId).ConfigureAwait(false);
            }
        }
    }

    private async Task RemoveFromIndexAsync(string accountName, byte serverId)
    {
        var indexName = GetIndexName(serverId);
        var (serverIndex, eTag) = await this._daprClient.GetStateAndETagAsync<HashSet<string>>(StoreName, indexName, ConsistencyMode.Strong).ConfigureAwait(false);
        if (serverIndex is null)
        {
            return;
        }

        if (serverIndex.Remove(accountName))
        {
            if (!await this._daprClient.TrySaveStateAsync(StoreName, indexName, serverIndex, eTag).ConfigureAwait(false))
            {
                // try again, if it failed
                await this.RemoveFromIndexAsync(accountName, serverId).ConfigureAwait(false);
            }
        }
    }

    private async Task SetAccountOfflineAsync(string accountName, byte serverId)
    {
        try
        {
            var (state, eTag) = await this.GetAccountStateAsync(accountName).ConfigureAwait(false);
            if (GetRegistration(state) != serverId)
            {
                // It's already offline, or logged in at another server, e.g. when this is a late log off.
                return;
            }

            await this._daprClient.TrySaveStateAsync<int?>(StoreName, accountName, OfflineServerId, eTag, metadata: DurableMetadata).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Couldn't get/set logged-out state for account {0}", accountName);
        }
    }

    private sealed record MapTransferReservation(Guid Credential, string CharacterName, byte SourceServerId, byte TargetServerId, DateTimeOffset ExpiresAt);
}
