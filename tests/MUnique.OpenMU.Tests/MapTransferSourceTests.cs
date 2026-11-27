// <copyright file="MapTransferSourceTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Buffers.Binary;
using System.Net;
using System.Threading;
using Moq;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameServer;
using MUnique.OpenMU.GameServer.RemoteView;
using MUnique.OpenMU.Network.Packets.ServerToClient;
using MUnique.OpenMU.Network.PlugIns;
using MUnique.OpenMU.Persistence;
using ExitGate = MUnique.OpenMU.Persistence.BasicModel.ExitGate;
using GameMapDefinition = MUnique.OpenMU.Persistence.BasicModel.GameMapDefinition;
using WarpInfo = MUnique.OpenMU.Persistence.BasicModel.WarpInfo;

/// <summary>Exercises the source warp with the game server's actual transfer packet writer.</summary>
[TestFixture]
public sealed class MapTransferSourceTests
{
    private const string AccountName = "transfer";
    private const byte DestinationServer = 2;
    private const ushort DestinationMap = 91;
    private const int InitialMoney = 9000;
    private const int WarpCost = 2000;
    private static readonly ClientVersion Version = new(106, 3, ClientLanguage.English);
    private static readonly IPEndPoint Destination = new(IPAddress.Parse("192.0.2.111"), 45906);

    /// <summary>A transfer saves while detached and exposes the credential only after success.</summary>
    /// <param name="saveSucceeded">Whether the first save is accepted.</param>
    /// <param name="targetAvailable">Whether a destination host is available.</param>
    [TestCase(true, true)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public async Task SourceSavesBeforeSendingTransferAsync(bool saveSucceeded, bool targetAvailable)
    {
        var login = new LoginServer.LoginServer();
        var persistence = new Mock<IPlayerContext>();
        var directory = new Mock<IMapTransferDirectory>();
        directory.Setup(d => d.FindDestinationAsync(It.IsAny<IGameServerContext>(), DestinationMap, Version))
            .ReturnsAsync(targetAvailable ? (DestinationServer, Destination) : ((byte, IPEndPoint)?)null);
        await using var game = LoginTestSetup.CreateGameServerContext(LoginTestSetup.CreatePersistence(persistence.Object), login,
            mapTransferDirectory: directory.Object);
        game.StopPeriodicTasks();
        game.Configuration.Maps.Single().IsActive = true;
        game.PlugInManager.DiscoverAndRegisterPlugIns(typeof(RemotePlayer).Assembly);
        var (player, output) = CastleSiegeRemoteViewTestHelper.CreatePlayer(game, Version);
        await using (player.ConfigureAwait(false))
        {
            using (output)
            {
                var character = PlayerTestHelper.CreateCharacter(game, Guid.NewGuid());
                character.Name = "Transfer01";
                var inventory = character.Inventory!;
                inventory.Money = InitialMoney;
                character.Attributes.Single(a => a.Definition == Stats.Level).Value = 1;
                Mock.Get(character).SetupGet(c => c.QuestStates).Returns(new List<CharacterQuestState>());
                persistence.Setup(p => p.GetPendingCastleSiegeRewardsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Array.Empty<CastleSiegePendingReward>());
                await game.AddPlayerAsync(player).ConfigureAwait(false);
                await PlayerTestHelper.EnterWorldAsync(player, character).ConfigureAwait(false);
                Assert.That(player.PlayerState.CurrentState, Is.SameAs(PlayerState.EnteredWorld));

                Assert.That(await login.TryLoginAsync(AccountName, game.Id).ConfigureAwait(false), Is.True);
                player.SetLoginServerRegistration(AccountName);
                player.PlayerDisconnected += departed => departed.ReleaseLoginServerRegistration() is { } name
                    ? login.LogOffAsync(name, game.Id)
                    : ValueTask.CompletedTask;
                var originalMap = character.CurrentMap;
                var originalPosition = player.Position;
                var targetMap = new GameMapDefinition { Id = Guid.NewGuid(), Number = (short)DestinationMap, IsActive = true };
                var gate = new ExitGate { Map = targetMap, X1 = 30, X2 = 30, Y1 = 40, Y2 = 40 };
                var saves = new List<(MUnique.OpenMU.DataModel.Configuration.GameMapDefinition? Map, int X, int Y, bool Detached, int Money, long Bytes)>();
                output.SetLength(0);
                persistence.Setup(p => p.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() =>
                {
                    saves.Add((character.CurrentMap, character.PositionX, character.PositionY,
                        player.CurrentMap is null && player.SelectedCharacter is null, inventory.Money, output.Length));
                    return saveSucceeded || saves.Count > 1;
                });

                await new WarpAction().WarpToAsync(player, new WarpInfo { Gate = gate, Costs = WarpCost }).ConfigureAwait(false);

                if (!targetAvailable)
                {
                    Assert.That(player.PlayerState.CurrentState, Is.SameAs(PlayerState.EnteredWorld));
                    Assert.That(player.CurrentMap?.Definition, Is.EqualTo(originalMap));
                    Assert.That(inventory.Money, Is.EqualTo(InitialMoney));
                    Assert.That(saves, Is.Empty);
                    return;
                }

                Assert.That(player.PlayerState.CurrentState, Is.SameAs(PlayerState.Finished));
                var bytesBeforeHandoff = saves[0].Bytes;
                Assert.That(saves[0], Is.EqualTo((targetMap, 30, 40, true, InitialMoney - WarpCost, bytesBeforeHandoff)));
                if (!saveSucceeded)
                {
                    Assert.That(saves, Has.Count.EqualTo(2));
                    Assert.That(saves[1], Is.EqualTo((originalMap, (int)originalPosition.X, (int)originalPosition.Y, true, InitialMoney, bytesBeforeHandoff)));
                    Assert.That(output.Length, Is.EqualTo(bytesBeforeHandoff));
                    Assert.That(await login.TryLoginAsync(AccountName, game.Id).ConfigureAwait(false), Is.True);
                    return;
                }

                Assert.That(saves, Has.Count.EqualTo(1));
                Assert.That(output.Length, Is.EqualTo(bytesBeforeHandoff + MapServerChangeInfo.Length));
                var packet = new MapServerChangeInfo(output.ToArray().AsMemory((int)bytesBeforeHandoff));
                Assert.That(packet.Header.SubCode, Is.EqualTo(MapServerChangeInfo.SubCode));
                Assert.That(packet.IpAddress, Is.EqualTo(Destination.Address.ToString()));
                Assert.That(packet.Port, Is.EqualTo(Destination.Port));
                Assert.That(packet.ServerGroup, Is.EqualTo(DestinationServer));
                var credential = ReadCredential(packet);
                Assert.That(await login.ClaimMapTransferAsync(credential, character.Name, DestinationServer).ConfigureAwait(false), Is.EqualTo(AccountName));
                Assert.That(await login.ClaimMapTransferAsync(credential, character.Name, DestinationServer).ConfigureAwait(false), Is.Null);
            }
        }
    }

    /// <summary>A siege entrant is transferred to its configured owner even when the source also hosts the map.</summary>
    [TestCase(true)]
    [TestCase(false)]
    public async Task SiegeWarpUsesOwnerWhenSourceHostsLocalCopyAsync(bool targetAvailable)
    {
        var login = new LoginServer.LoginServer();
        var persistence = new Mock<IPlayerContext>();
        var directory = new Mock<IMapTransferDirectory>();
        var ownership = new Mock<IMapOwnershipLocator>();
        ownership.Setup(locator => locator.GetOwnerServerIdAsync(It.IsAny<IGameServerContext>(), DestinationMap)).ReturnsAsync((byte?)DestinationServer);
        directory.Setup(locator => locator.FindDestinationOnServerAsync(It.IsAny<IGameServerContext>(), DestinationServer, DestinationMap, Version))
            .ReturnsAsync(targetAvailable ? (DestinationServer, Destination) : ((byte, IPEndPoint)?)null);
        await using var game = LoginTestSetup.CreateGameServerContext(
            LoginTestSetup.CreatePersistence(persistence.Object),
            login,
            mapTransferDirectory: directory.Object,
            mapOwnershipLocator: ownership.Object);
        game.StopPeriodicTasks();
        game.Configuration.Maps.Single().IsActive = true;
        game.PlugInManager.DiscoverAndRegisterPlugIns(typeof(RemotePlayer).Assembly);
        var siegeMap = new Persistence.BasicModel.GameMapDefinition
        {
            Id = Guid.NewGuid(),
            Number = (short)DestinationMap,
            IsActive = true,
            TerrainData = new byte[ushort.MaxValue + 3],
        };
        game.Configuration.Maps.Add(siegeMap);
        game.ServerConfiguration.Maps.Add(siegeMap);
        game.Configuration.CastleSiegeConfiguration = new Persistence.BasicModel.CastleSiegeConfiguration
        {
            Enabled = true,
            CastleSiegeMapDefinition = siegeMap,
        };
        var (player, output) = CastleSiegeRemoteViewTestHelper.CreatePlayer(game, Version);
        await using (player.ConfigureAwait(false))
        {
            using (output)
            {
                var character = PlayerTestHelper.CreateCharacter(game, Guid.NewGuid());
                character.Name = "SiegeTransfer01";
                var inventory = character.Inventory!;
                inventory.Money = InitialMoney;
                character.Attributes.Single(attribute => attribute.Definition == Stats.Level).Value = 1;
                Mock.Get(character).SetupGet(value => value.QuestStates).Returns(new List<CharacterQuestState>());
                persistence.Setup(value => value.GetPendingCastleSiegeRewardsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Array.Empty<CastleSiegePendingReward>());
                await game.AddPlayerAsync(player).ConfigureAwait(false);
                await PlayerTestHelper.EnterWorldAsync(player, character).ConfigureAwait(false);
                Assert.That(await login.TryLoginAsync(AccountName, game.Id).ConfigureAwait(false), Is.True);
                player.SetLoginServerRegistration(AccountName);
                player.PlayerDisconnected += departed => departed.ReleaseLoginServerRegistration() is { } name
                    ? login.LogOffAsync(name, game.Id)
                    : ValueTask.CompletedTask;
                persistence.Setup(value => value.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

                var gate = new ExitGate { Map = siegeMap, X1 = 30, X2 = 30, Y1 = 40, Y2 = 40 };
                output.SetLength(0);
                await new WarpAction().WarpToAsync(player, new WarpInfo { Gate = gate, Costs = WarpCost }).ConfigureAwait(false);

                directory.Verify(value => value.FindDestinationOnServerAsync(game, DestinationServer, DestinationMap, Version), Times.Once);
                if (!targetAvailable)
                {
                    Assert.That(player.PlayerState.CurrentState, Is.SameAs(PlayerState.EnteredWorld));
                    Assert.That(player.CurrentMap, Is.Not.Null);
                    Assert.That(inventory.Money, Is.EqualTo(InitialMoney));
                    Assert.That(output.Length, Is.GreaterThan(0), "the client receives the map-change failure response");
                    return;
                }

                Assert.That(player.PlayerState.CurrentState, Is.SameAs(PlayerState.Finished));
                Assert.That(output.Length, Is.GreaterThanOrEqualTo(MapServerChangeInfo.Length));
                var outputBytes = output.ToArray();
                var packet = new MapServerChangeInfo(new Memory<byte>(outputBytes, outputBytes.Length - MapServerChangeInfo.Length, MapServerChangeInfo.Length));
                Assert.That(packet.ServerGroup, Is.EqualTo(DestinationServer));
                Assert.That(inventory.Money, Is.EqualTo(InitialMoney - WarpCost));
            }
        }
    }

    private static Guid ReadCredential(MapServerChangeInfo packet)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, packet.AuthenticationCode1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], packet.AuthenticationCode2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], packet.AuthenticationCode3);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..], packet.AuthenticationCode4);
        return new Guid(bytes);
    }
}
