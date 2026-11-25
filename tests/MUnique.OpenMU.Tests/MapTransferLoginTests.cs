// <copyright file="MapTransferLoginTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

/// <summary>
/// Verifies the login registry's single-use map-transfer reservations.
/// </summary>
[TestFixture]
public sealed class MapTransferLoginTests
{
    /// <summary>Only the selected destination and character can claim the reserved account.</summary>
    [Test]
    public async Task TransferBlocksOtherLoginsAndRequiresTheTargetAndCharacterAsync()
    {
        var loginServer = new LoginServer.LoginServer();
        Assert.That(await loginServer.TryLoginAsync("account", 1).ConfigureAwait(false), Is.True);
        Assert.That(await loginServer.BeginMapTransferAsync("account", "Character", 3, 2).ConfigureAwait(false), Is.Null);
        Assert.That(await loginServer.BeginMapTransferAsync("account", "Character", 1, 1).ConfigureAwait(false), Is.Null);

        var credential = await loginServer.BeginMapTransferAsync("account", "Character", 1, 2).ConfigureAwait(false);
        Assert.That(credential, Is.Not.Null);
        Assert.That(await loginServer.BeginMapTransferAsync("account", "Character", 1, 2).ConfigureAwait(false), Is.Null);
        Assert.That(await loginServer.TryLoginAsync("account", 3).ConfigureAwait(false), Is.False);
        Assert.That(await loginServer.TryReserveAccountAsync("account", 1000).ConfigureAwait(false), Is.False);
        Assert.That(await loginServer.GetSnapshotAsync().ConfigureAwait(false), Is.Empty);

        await loginServer.LogOffAsync("account", 1).ConfigureAwait(false);
        Assert.That(await loginServer.ClaimMapTransferAsync(credential!.Value, "Character", 3).ConfigureAwait(false), Is.Null);
        Assert.That(await loginServer.ClaimMapTransferAsync(credential.Value, "OtherCharacter", 2).ConfigureAwait(false), Is.Null);
        Assert.That(await loginServer.ClaimMapTransferAsync(credential.Value, "Character", 2).ConfigureAwait(false), Is.EqualTo("account"));
        Assert.That(await loginServer.ClaimMapTransferAsync(credential.Value, "Character", 2).ConfigureAwait(false), Is.Null);

        await loginServer.LogOffAsync("account", 1).ConfigureAwait(false);
        Assert.That(await loginServer.TryLoginAsync("account", 3).ConfigureAwait(false), Is.False);
        Assert.That(await loginServer.GetSnapshotAsync().ConfigureAwait(false), Is.EquivalentTo(
            new Dictionary<string, byte> { ["account"] = 2 }));
    }

    /// <summary>An expired reservation cannot be claimed and cannot hold up a reconnect.</summary>
    [Test]
    public async Task ExpiredTransferReleasesAccountAsync()
    {
        var clock = new TransferClock();
        var loginServer = new LoginServer.LoginServer(clock);
        Assert.That(await loginServer.TryLoginAsync("account", 1).ConfigureAwait(false), Is.True);
        var credential = await loginServer.BeginMapTransferAsync("account", "Character", 1, 2).ConfigureAwait(false);
        Assert.That(credential, Is.Not.Null);

        clock.Now = clock.Now.AddSeconds(29);
        Assert.That(await loginServer.TryLoginAsync("account", 3).ConfigureAwait(false), Is.False);
        clock.Now = clock.Now.AddSeconds(1);

        Assert.That(await loginServer.TryLoginAsync("account", 3).ConfigureAwait(false), Is.True);
        Assert.That(await loginServer.ClaimMapTransferAsync(credential!.Value, "Character", 2).ConfigureAwait(false), Is.Null);
        Assert.That(await loginServer.GetSnapshotAsync().ConfigureAwait(false), Is.EquivalentTo(
            new Dictionary<string, byte> { ["account"] = 3 }));
    }

    /// <summary>Only the source can cancel, which allows the account to log in again.</summary>
    [Test]
    public async Task CancelTransferRequiresSourceAndReleasesAccountAsync()
    {
        var loginServer = new LoginServer.LoginServer();
        Assert.That(await loginServer.TryLoginAsync("account", 1).ConfigureAwait(false), Is.True);
        var credential = await loginServer.BeginMapTransferAsync("account", "Character", 1, 2).ConfigureAwait(false);
        Assert.That(credential, Is.Not.Null);

        Assert.That(await loginServer.CancelMapTransferAsync(credential!.Value, 2).ConfigureAwait(false), Is.False);
        Assert.That(await loginServer.TryLoginAsync("account", 3).ConfigureAwait(false), Is.False);
        Assert.That(await loginServer.CancelMapTransferAsync(credential.Value, 1).ConfigureAwait(false), Is.True);
        Assert.That(await loginServer.ClaimMapTransferAsync(credential.Value, "Character", 2).ConfigureAwait(false), Is.Null);
        Assert.That(await loginServer.TryLoginAsync("account", 3).ConfigureAwait(false), Is.True);
    }

    /// <summary>Concurrent claims of the same reservation establish exactly one session.</summary>
    [Test]
    public async Task OnlyOneConcurrentClaimSucceedsAsync()
    {
        var loginServer = new LoginServer.LoginServer();
        Assert.That(await loginServer.TryLoginAsync("account", 1).ConfigureAwait(false), Is.True);
        var credential = await loginServer.BeginMapTransferAsync("account", "Character", 1, 2).ConfigureAwait(false);
        Assert.That(credential, Is.Not.Null);
        var claims = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => loginServer.ClaimMapTransferAsync(credential!.Value, "Character", 2).AsTask()));

        var results = await Task.WhenAll(claims).ConfigureAwait(false);

        Assert.That(results.Count(account => account == "account"), Is.EqualTo(1));
        Assert.That(await loginServer.GetSnapshotAsync().ConfigureAwait(false), Is.EquivalentTo(
            new Dictionary<string, byte> { ["account"] = 2 }));
    }

    private sealed class TransferClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => this.Now;
    }
}
