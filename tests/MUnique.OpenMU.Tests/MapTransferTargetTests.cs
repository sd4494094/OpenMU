// <copyright file="MapTransferTargetTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Buffers.Binary;
using System.Threading;
using Moq;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameServer;
using MUnique.OpenMU.GameServer.MessageHandler.Login;
using MUnique.OpenMU.Network.Packets.ClientToServer;
using MUnique.OpenMU.Network.PlugIns;
using MUnique.OpenMU.Network.Xor;
using MUnique.OpenMU.Persistence;

/// <summary>Exercises transfer authentication at a receiving game server.</summary>
[TestFixture]
public sealed class MapTransferTargetTests
{
    private const string LoginName = "transfer";
    private const string CharacterName = "Transfer01";
    private const byte SourceServerId = 1;
    private static readonly ClientVersion Version = new(106, 3, ClientLanguage.English);
    private static readonly byte[] VersionBytes = "20404"u8.ToArray();

    /// <summary>A valid transfer selects the saved character and acknowledges on the target connection.</summary>
    /// <param name="blockedLanding">Whether the saved landing is blocked on the receiving server.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async Task AuthenticatesAndEntersWithTheSavedCharacterAsync(bool blockedLanding)
    {
        var logins = new LoginServer.LoginServer();
        var persistence = new Mock<IPlayerContext>();
        await using var game = CreateGame(persistence, logins);
        var account = CreateAccount(game);
        var character = account.Characters.Single();
        character.PositionX = 30;
        character.PositionY = 31;
        var terrain = game.Configuration.Maps.Single().TerrainData!;
        terrain[3 + (22 * 256) + 21] = (byte)TerrainAttributeType.Safezone;
        if (blockedLanding)
        {
            terrain[3 + (31 * 256) + 30] = (byte)TerrainAttributeType.Blocked;
            persistence.Setup(p => p.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(() =>
            {
                Assert.That((character.PositionX, character.PositionY), Is.EqualTo((21, 22)));
                return ValueTask.FromResult(true);
            });
        }

        persistence.Setup(p => p.GetAccountByLoginNameAsync(LoginName, It.IsAny<CancellationToken>())).ReturnsAsync(account);
        var credential = await ReserveAsync(logins, game.Id).ConfigureAwait(false);
        var (player, output) = CastleSiegeRemoteViewTestHelper.CreatePlayer(game, Version);
        await using (player.ConfigureAwait(false))
        {
            using (output)
            {
                await ReadyAsync(game, player).ConfigureAwait(false);
                await player.MainPacketHandler.HandlePacketAsync(player, CreatePacket(credential)).ConfigureAwait(false);

                Assert.That(player.PlayerState.CurrentState, Is.SameAs(PlayerState.EnteredWorld));
                Assert.That(player.SelectedCharacter, Is.SameAs(account.Characters.Single()));
                Assert.That(player.CurrentMap?.Definition, Is.SameAs(account.Characters.Single().CurrentMap));
                Assert.That((character.PositionX, character.PositionY), Is.EqualTo(blockedLanding ? (21, 22) : (30, 31)));
                Assert.That(output.ToArray()[^4..], Is.EqualTo(new byte[] { 0xC1, 4, 0xB1, 1 }));

                await player.MainPacketHandler.HandlePacketAsync(player, CreatePacket(credential)).ConfigureAwait(false);
                Assert.That(player.PlayerState.CurrentState, Is.SameAs(PlayerState.EnteredWorld));
                persistence.Verify(p => p.GetAccountByLoginNameAsync(LoginName, It.IsAny<CancellationToken>()), Times.Once);
                Assert.That((await logins.GetSnapshotAsync().ConfigureAwait(false))[LoginName], Is.EqualTo(game.Id));
            }
        }
    }

    /// <summary>Malformed or incompatible frames never consume a reservation or load an account.</summary>
    /// <param name="variant">The packet rejection case.</param>
    [TestCase("plain")]
    [TestCase("truncated")]
    [TestCase("wrong-version")]
    [TestCase("wrong-credential")]
    public async Task RejectsInvalidAuthenticationWithoutConsumingTheReservationAsync(string variant)
    {
        var logins = new LoginServer.LoginServer();
        var persistence = new Mock<IPlayerContext>();
        await using var game = CreateGame(persistence, logins);
        var credential = await ReserveAsync(logins, game.Id).ConfigureAwait(false);
        var (player, output) = CastleSiegeRemoteViewTestHelper.CreatePlayer(game, Version);
        await using (player.ConfigureAwait(false))
        {
            using (output)
            {
                await ReadyAsync(game, player).ConfigureAwait(false);
                var packet = CreatePacket(variant == "wrong-credential" ? Guid.NewGuid() : credential);
                switch (variant)
                {
                    case "plain":
                        packet[0] = 0xC1;
                        break;
                    case "wrong-version":
                        packet[48] ^= 1;
                        break;
                    case "truncated":
                        packet = packet[..^1];
                        packet[1] = (byte)packet.Length;
                        break;
                }

                await player.MainPacketHandler.HandlePacketAsync(player, packet).ConfigureAwait(false);

                Assert.That(player.PlayerState.CurrentState, Is.SameAs(PlayerState.Finished));
                Assert.That(output.Length, Is.Zero);
                persistence.Verify(p => p.GetAccountByLoginNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
                Assert.That(await logins.ClaimMapTransferAsync(credential, CharacterName, game.Id).ConfigureAwait(false), Is.EqualTo(LoginName));
            }
        }
    }

    /// <summary>A failed destination load releases the claimed registration during disconnection.</summary>
    /// <param name="banned">Whether an existing but banned account is returned.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async Task DisconnectAfterFailedLoadReleasesRegistrationAsync(bool banned)
    {
        var logins = new LoginServer.LoginServer();
        var persistence = new Mock<IPlayerContext>();
        await using var game = CreateGame(persistence, logins);
        var account = banned ? CreateAccount(game) : null;
        account?.State = AccountState.Banned;
        persistence.Setup(p => p.GetAccountByLoginNameAsync(LoginName, It.IsAny<CancellationToken>())).ReturnsAsync(account);
        var credential = await ReserveAsync(logins, game.Id).ConfigureAwait(false);
        var (player, output) = CastleSiegeRemoteViewTestHelper.CreatePlayer(game, Version);
        await using (player.ConfigureAwait(false))
        {
            using (output)
            {
                await ReadyAsync(game, player).ConfigureAwait(false);
                player.PlayerDisconnected += departed => departed.ReleaseLoginServerRegistration() is { } name
                    ? logins.LogOffAsync(name, game.Id)
                    : ValueTask.CompletedTask;
                await player.MainPacketHandler.HandlePacketAsync(player, CreatePacket(credential)).ConfigureAwait(false);

                Assert.That(player.PlayerState.CurrentState, Is.SameAs(PlayerState.Finished));
                Assert.That(player.SelectedCharacter, Is.Null);
                Assert.That(output.Length, Is.Zero);
                Assert.That(await logins.TryLoginAsync(LoginName, SourceServerId).ConfigureAwait(false), Is.True);
            }
        }
    }

    private static byte[] CreatePacket(Guid credential)
    {
        var bytes = new byte[ServerChangeAuthentication.Length];
        var packet = new ServerChangeAuthenticationRef(bytes);
        VersionBytes.CopyTo(packet.ClientVersion);
        Encoding.UTF8.GetBytes(CharacterName, packet.CharacterNameXor3);
        new Xor3Decryptor(0).Decrypt(packet.CharacterNameXor3[..10]);
        var guid = credential.ToByteArray();
        packet.AuthCode1 = BinaryPrimitives.ReadUInt32LittleEndian(guid);
        packet.AuthCode2 = BinaryPrimitives.ReadUInt32LittleEndian(guid.AsSpan(4));
        packet.AuthCode3 = BinaryPrimitives.ReadUInt32LittleEndian(guid.AsSpan(8));
        packet.AuthCode4 = BinaryPrimitives.ReadUInt32LittleEndian(guid.AsSpan(12));
        return bytes;
    }

    private static GameServerContext CreateGame(Mock<IPlayerContext> persistence, LoginServer.LoginServer logins)
    {
        ClientVersionResolver.Register(VersionBytes, Version);
        persistence.Setup(p => p.GetPendingCastleSiegeRewardsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<CastleSiegePendingReward>());
        var game = LoginTestSetup.CreateGameServerContext(LoginTestSetup.CreatePersistence(persistence.Object), logins);
        game.StopPeriodicTasks();
        game.Configuration.Maps.Single().IsActive = true;
        game.PlugInManager.DiscoverAndRegisterPlugIns(typeof(MapTransferAuthenticationHandlerPlugIn).Assembly);
        return game;
    }

    private static Account CreateAccount(GameServerContext game)
    {
        var character = PlayerTestHelper.CreateCharacter(game);
        character.Name = CharacterName;
        character.Attributes.Single(attribute => attribute.Definition == Stats.Level).Value = 1;
        Mock.Get(character).SetupGet(c => c.QuestStates).Returns(new List<CharacterQuestState>());
        var account = new Mock<Account>();
        account.SetupAllProperties();
        account.Object.LoginName = LoginName;
        account.SetupGet(a => a.Attributes).Returns(new List<StatAttribute>());
        account.SetupGet(a => a.UnlockedCharacterClasses).Returns(new List<CharacterClass>());
        account.SetupGet(a => a.Characters).Returns(new List<Character> { character });
        return account.Object;
    }

    private static async ValueTask<Guid> ReserveAsync(LoginServer.LoginServer logins, byte target)
    {
        Assert.That(await logins.TryLoginAsync(LoginName, SourceServerId).ConfigureAwait(false), Is.True);
        var credential = await logins.BeginMapTransferAsync(LoginName, CharacterName, SourceServerId, target).ConfigureAwait(false);
        Assert.That(credential, Is.Not.Null);
        return credential!.Value;
    }

    private static async ValueTask ReadyAsync(GameServerContext game, Player player)
    {
        await game.AddPlayerAsync(player).ConfigureAwait(false);
        Assert.That(await player.PlayerState.TryAdvanceToAsync(PlayerState.LoginScreen).ConfigureAwait(false), Is.True);
    }
}
