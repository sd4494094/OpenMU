// <copyright file="PersistentMapTransferTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Text.Json;
using global::Dapr.Client;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.CentralServer.Host;

/// <summary>
/// Exercises the login registry against a disposable Dapr Redis state store.
/// </summary>
[TestFixture]
public sealed class PersistentMapTransferTests
{
    private const string StoreName = "login-state";
    private const string Character = "Character";
    private const byte Source = 1;
    private const byte Target = 2;

    /// <summary>The account remains reserved during transfer and exactly one claimant can register it.</summary>
    [Test]
    public async Task ConcurrentClaimsAgainstRedisHaveOneWinnerAsync()
    {
        using var client = Connect();
        using var otherClient = Connect();
        var source = CreateServer(client);
        var target = CreateServer(otherClient);
        var account = NewAccount();
        Assert.That(await source.TryLoginAsync(account, Source).ConfigureAwait(false), Is.True);
        var credential = await source.BeginMapTransferAsync(account, Character, Source, Target).ConfigureAwait(false);
        Assert.That(credential, Is.Not.Null);
        Assert.That(await target.TryLoginAsync(account, Target).ConfigureAwait(false), Is.False);
        Assert.That(await target.TryReserveAccountAsync(account, 1000).ConfigureAwait(false), Is.False);
        Assert.That(await target.GetSnapshotAsync().ConfigureAwait(false), Does.Not.ContainKey(account));
        Assert.That(await target.ClaimMapTransferAsync(credential!.Value, Character, 3).ConfigureAwait(false), Is.Null);
        Assert.That(await target.ClaimMapTransferAsync(credential.Value, "Other", Target).ConfigureAwait(false), Is.Null);

        var claims = await Task.WhenAll(
            source.ClaimMapTransferAsync(credential.Value, Character, Target).AsTask(),
            target.ClaimMapTransferAsync(credential.Value, Character, Target).AsTask()).ConfigureAwait(false);
        Assert.That(claims.Count(result => result == account), Is.EqualTo(1));
        var sourceIndex = await client.GetStateAsync<HashSet<string>>(StoreName, "serverindex-1").ConfigureAwait(false);
        Assert.That(sourceIndex, Does.Not.Contain(account));
        Assert.That(await target.ClaimMapTransferAsync(credential.Value, Character, Target).ConfigureAwait(false), Is.Null);
        await source.LogOffAsync(account, Source).ConfigureAwait(false);
        Assert.That((await target.GetSnapshotAsync().ConfigureAwait(false))[account], Is.EqualTo(Target));

        await target.RemoveServerAsync(Target).ConfigureAwait(false);
        Assert.That(await target.GetSnapshotAsync().ConfigureAwait(false), Does.Not.ContainKey(account));
        Assert.That(await source.TryLoginAsync(account, Source).ConfigureAwait(false), Is.True);
        await source.LogOffAsync(account, Source).ConfigureAwait(false);
    }

    /// <summary>Timeout releases the account while retaining its state key and version history.</summary>
    [Test]
    public async Task ExpiredRedisReservationCanBeReplacedByAnOrdinaryLoginAsync()
    {
        using var client = Connect();
        var clock = new TransferClock();
        var login = new PersistentLoginServer(NullLogger<PersistentLoginServer>.Instance, client, clock);
        var account = NewAccount();
        Assert.That(await login.TryLoginAsync(account, Source).ConfigureAwait(false), Is.True);
        var credential = await login.BeginMapTransferAsync(account, Character, Source, Target).ConfigureAwait(false);
        Assert.That(credential, Is.Not.Null);
        var (reserved, reservedTag) = await client.GetStateAndETagAsync<JsonElement>(StoreName, account).ConfigureAwait(false);
        Assert.That(reserved.ValueKind, Is.EqualTo(JsonValueKind.Object));

        clock.Now = clock.Now.AddSeconds(30);
        Assert.That(await login.ClaimMapTransferAsync(credential!.Value, Character, Target).ConfigureAwait(false), Is.Null);
        Assert.That(await login.TryLoginAsync(account, Target).ConfigureAwait(false), Is.True);
        var (owner, ownerTag) = await client.GetStateAndETagAsync<JsonElement>(StoreName, account).ConfigureAwait(false);
        Assert.That(owner.GetInt32(), Is.EqualTo(Target));
        Assert.That(ownerTag, Is.Not.EqualTo(reservedTag));
        Assert.That(await login.CancelMapTransferAsync(credential.Value, Source).ConfigureAwait(false), Is.False);
        await login.LogOffAsync(account, Target).ConfigureAwait(false);
    }

    /// <summary>Only the issuing source can cancel, and invalid credentials leave state unchanged.</summary>
    [Test]
    public async Task RedisCancellationChecksSourceAndInvalidatesTheCredentialAsync()
    {
        using var client = Connect();
        var login = CreateServer(client);
        var account = NewAccount();
        Assert.That(await login.TryLoginAsync(account, Source).ConfigureAwait(false), Is.True);
        Assert.That(await login.BeginMapTransferAsync(account, Character, Target, 3).ConfigureAwait(false), Is.Null);
        var credential = await login.BeginMapTransferAsync(account, Character, Source, Target).ConfigureAwait(false);
        Assert.That(credential, Is.Not.Null);
        Assert.That(await login.ClaimMapTransferAsync(Guid.NewGuid(), Character, Target).ConfigureAwait(false), Is.Null);
        Assert.That(await login.CancelMapTransferAsync(credential!.Value, Target).ConfigureAwait(false), Is.False);
        Assert.That(await login.CancelMapTransferAsync(credential.Value, Source).ConfigureAwait(false), Is.True);
        Assert.That(await login.ClaimMapTransferAsync(credential.Value, Character, Target).ConfigureAwait(false), Is.Null);
        Assert.That(await login.TryReserveAccountAsync(account, 767).ConfigureAwait(false), Is.True);
        Assert.That(await login.TryLoginAsync(account, Source).ConfigureAwait(false), Is.False);
        await login.ReleaseAccountReservationAsync(account, 767).ConfigureAwait(false);
        Assert.That(await login.TryLoginAsync(account, Source).ConfigureAwait(false), Is.True);
        await login.LogOffAsync(account, Source).ConfigureAwait(false);
    }

    private static DaprClient Connect()
    {
        var endpoint = Environment.GetEnvironmentVariable("OPENMU_TEST_MAP_TRANSFER_DAPR_GRPC");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            Assert.Ignore("Set OPENMU_TEST_MAP_TRANSFER_DAPR_GRPC to a disposable Dapr Redis state store.");
        }

        return new DaprClientBuilder().UseGrpcEndpoint(endpoint!).Build();
    }

    private static PersistentLoginServer CreateServer(DaprClient client)
        => new(NullLogger<PersistentLoginServer>.Instance, client);

    private static string NewAccount() => Guid.NewGuid().ToString("N")[..10];

    private sealed class TransferClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => this.Now;
    }
}
