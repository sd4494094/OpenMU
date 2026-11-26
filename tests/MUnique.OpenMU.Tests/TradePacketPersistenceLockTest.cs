// <copyright file="TradePacketPersistenceLockTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Buffers;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlayerActions.Trade;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.GameServer;
using MUnique.OpenMU.GameServer.MessageHandler;
using MUnique.OpenMU.GameServer.MessageHandler.Trade;
using MUnique.OpenMU.GameServer.RemoteView;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Network;
using MUnique.OpenMU.Network.PlugIns;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Exercises trade opening through the actual remote packet event and persistence funnel.
/// </summary>
[TestFixture]
public class TradePacketPersistenceLockTest
{
    /// <summary>
    /// A partner's packet holding its persistence lock must be able to cancel an incoming acceptance.
    /// </summary>
    /// <param name="reversePlayers">Whether the requester is created after the accepter.</param>
    /// <returns>The task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task AcceptanceAndPartnerCancellationDoNotDeadlockAsync(bool reversePlayers)
    {
        var manager = new PlugInManager(null, NullLoggerFactory.Instance, null, null);
        manager.RegisterPlugIn<IPacketHandlerPlugIn, TradeAcceptHandlerPlugIn>();
        manager.RegisterPlugIn<IPacketHandlerPlugIn, GatedCancellationHandler>();
        var contexts = new List<Mock<IPlayerContext>>();
        var configuration = PlayerTestHelper.CreateGameConfiguration();
        var provider = new Mock<IPersistenceContextProvider>();
        provider.Setup(p => p.CreateNewPlayerContext(It.IsAny<GameConfiguration>())).Returns(() =>
        {
            var context = new Mock<IPlayerContext>();
            context.Setup(p => p.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(true));
            contexts.Add(context);
            return context.Object;
        });
        var initializer = new MapInitializer(configuration, NullLogger<MapInitializer>.Instance, NullDropGenerator.Instance, null);
        await using var game = new GameServerContext(
            new GameServerDefinition { GameConfiguration = configuration, ServerConfiguration = new GameServerConfiguration() },
            new Mock<IGuildServer>().Object, new Mock<IEventPublisher>().Object,
            new Mock<ILoginServer>().Object, new Mock<IFriendServer>().Object,
            provider.Object, initializer, NullLoggerFactory.Instance, manager, NullDropGenerator.Instance, new ConfigurationChangeMediator());
        game.StopPeriodicTasks();
        initializer.PlugInManager = manager;
        initializer.PathFinderPool = game.PathFinderPool;
        var first = await CreatePlayerAsync(game).ConfigureAwait(false);
        var second = await CreatePlayerAsync(game).ConfigureAwait(false);
        var requester = reversePlayers ? second : first;
        var accepter = reversePlayers ? first : second;
        Assert.That(await new TradeRequestAction().RequestTradeAsync(requester.Player, accepter.Player).ConfigureAwait(false), Is.True);

        var saveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        contexts[reversePlayers ? 0 : 1].Setup(p => p.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(async () =>
        {
            saveEntered.TrySetResult();
            await releaseSave.Task.ConfigureAwait(false);
            return true;
        });
        var handler = (GatedCancellationHandler)requester.Player.MainPacketHandler[GatedCancellationHandler.Code]!;
        var cancel = requester.Receive(new ReadOnlySequence<byte>(new byte[] { 0xC1, 3, GatedCancellationHandler.Code })).AsTask();
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // Calling the real event runs synchronously until its first incomplete await. In the old
        // implementation this enters the accepter's save while holding both trade locks. The fixed
        // path waits for both persistence locks first, so the partner can finish its packet instead.
        var accept = accepter.Receive(new ReadOnlySequence<byte>(new byte[] { 0xC1, 4, 0x37, 1 })).AsTask();
        handler.Release.TrySetResult();
        releaseSave.TrySetResult();
        await Task.WhenAll(cancel, accept).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.That(saveEntered.Task.IsCompleted, Is.False, "Cancellation wins before opening either inventory.");
        foreach (var player in new[] { requester.Player, accepter.Player })
        {
            Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.EnteredWorld));
            Assert.That(player.TradingPartner, Is.Null);
            Assert.That(player.BackupInventory, Is.Null);
        }
    }

    /// <summary>
    /// Ordinary handlers retain their mutation lock after dispatch moves out of RemotePlayer.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task OrdinaryHandlerStillBlocksProgressSaveAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var version = new Mock<IClientVersionProvider>();
        var manager = new PlugInManager(null, NullLoggerFactory.Instance, null, null);
        using var container = new TestPacketContainer(version.Object, manager);
        var handler = new GatedCancellationHandler(version.Object);
        var packet = container.DispatchAsync(player, handler).AsTask();
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var save = player.SaveProgressAsync().AsTask();
        try
        {
            Assert.That(save.IsCompleted, Is.False, "A save must wait for an ordinary packet mutation.");
        }
        finally
        {
            handler.Release.TrySetResult();
            await Task.WhenAll(packet, save).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    private static async ValueTask<(TestRemotePlayer Player, AsyncEventHandler<ReadOnlySequence<byte>> Receive)> CreatePlayerAsync(GameServerContext game)
    {
        AsyncEventHandler<ReadOnlySequence<byte>>? receive = null;
        var connection = new Mock<IConnection>();
        connection.SetupAdd(c => c.PacketReceived += It.IsAny<AsyncEventHandler<ReadOnlySequence<byte>>>())
            .Callback<AsyncEventHandler<ReadOnlySequence<byte>>>(handler => receive = handler);
        var player = new TestRemotePlayer(game, connection.Object);
        await PlayerTestHelper.EnterWorldAsync(player, PlayerTestHelper.CreateCharacter(game)).ConfigureAwait(false);
        return (player, receive!);
    }

    /// <summary>
    /// Holds the ordinary packet funnel before cancelling a trade.
    /// </summary>
    public sealed class GatedCancellationHandler : IPacketHandlerPlugIn
    {
        /// <summary>
        /// Gets the otherwise unused packet key selected by this fixture.
        /// </summary>
        public const byte Code = 0x04;

        /// <summary>
        /// Initializes a new instance of the <see cref="GatedCancellationHandler"/> class.
        /// </summary>
        /// <param name="client">The version provider ensures that this gated handler belongs to one connection.</param>
        public GatedCancellationHandler(IClientVersionProvider client)
        {
        }

        /// <inheritdoc />
        public byte Key => Code;

        /// <inheritdoc />
        public bool IsEncryptionExpected => false;

        /// <summary>
        /// Gets the signal that this packet holds its player's persistence lock.
        /// </summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Gets the signal which lets the packet cancel its trade.
        /// </summary>
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <inheritdoc />
        public async ValueTask HandlePacketAsync(Player player, Memory<byte> packet)
        {
            this.Entered.TrySetResult();
            await this.Release.Task.ConfigureAwait(false);
            await new TradeCancelAction().CancelTradeAsync(player).ConfigureAwait(false);
        }
    }

    private sealed class TestRemotePlayer(IGameServerContext game, IConnection connection)
        : RemotePlayer(game, connection, new ClientVersion(6, 3, ClientLanguage.Invariant))
    {
        protected override ICustomPlugInContainer<IViewPlugIn> CreateViewPlugInContainer() => new MockViewPlugInContainer();
    }

    private sealed class TestPacketContainer(IClientVersionProvider client, PlugInManager manager)
        : PacketHandlerPlugInContainer<IPacketHandlerPlugIn>(client, manager, NullLoggerFactory.Instance)
    {
        internal ValueTask DispatchAsync(Player player, IPacketHandlerPlugIn handler)
            => this.HandlePacketAsync(player, new byte[] { 0xC1, 3, GatedCancellationHandler.Code }, handler);
    }
}
