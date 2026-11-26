// <copyright file="PlayerShopPacketPersistenceTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Buffers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlayerShopCurrencies;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.GameLogic.Views.Inventory;
using MUnique.OpenMU.GameServer;
using MUnique.OpenMU.GameServer.MessageHandler;
using MUnique.OpenMU.GameServer.MessageHandler.PlayerShop;
using MUnique.OpenMU.GameServer.RemoteView;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Network;
using MUnique.OpenMU.Network.Packets.ClientToServer;
using MUnique.OpenMU.Network.PlugIns;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Exercises the real player-shop group and remote packet funnel with competing owner requests.
/// </summary>
[TestFixture]
public class PlayerShopPacketPersistenceTest
{
    /// <summary>
    /// Buying enters the paired scope before either player's lock, while ordinary shop requests retain theirs.
    /// </summary>
    /// <param name="reversePlayers">Whether the seller is created after the buyer.</param>
    /// <returns>The task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task PurchaseWaitsForOwnerPacketAsync(bool reversePlayers)
    {
        var manager = new PlugInManager(null, NullLoggerFactory.Instance, null, null);
        manager.RegisterPlugIn<IPacketHandlerPlugIn, StoreHandlerGroupPlugIn>();
        manager.RegisterPlugIn<ISubPacketHandlerPlugIn, PlayerShopBuyRequestPacketHandlerPlugIn>();
        manager.RegisterPlugIn<ISubPacketHandlerPlugIn, GatedStoreHandler>();
        var configuration = PlayerTestHelper.CreateGameConfiguration();
        configuration.MaximumInventoryMoney = 1000;
        var memory = new InMemoryPersistenceContextProvider();
        var trade = new Mock<ITradeContext>();
        trade.Setup(c => c.SaveChangesAsync(It.IsAny<System.Threading.CancellationToken>())).Returns(new ValueTask<bool>(true));
        var provider = new Mock<IPersistenceContextProvider>();
        provider.Setup(p => p.CreateNewPlayerContext(It.IsAny<GameConfiguration>())).Returns<GameConfiguration>(memory.CreateNewPlayerContext);
        provider.Setup(p => p.CreateNewTradeContext()).Returns(trade.Object);
        var maps = new MapInitializer(configuration, NullLogger<MapInitializer>.Instance, NullDropGenerator.Instance, null);
        await using var game = new GameServerContext(
            new GameServerDefinition { GameConfiguration = configuration, ServerConfiguration = new GameServerConfiguration() },
            new Mock<IGuildServer>().Object, new Mock<IEventPublisher>().Object,
            new Mock<ILoginServer>().Object, new Mock<IFriendServer>().Object,
            provider.Object, maps, NullLoggerFactory.Instance, manager, NullDropGenerator.Instance, new ConfigurationChangeMediator());
        game.StopPeriodicTasks();
        maps.PlugInManager = manager;
        maps.PathFinderPool = game.PathFinderPool;
        var first = await CreatePlayerAsync(game, "First").ConfigureAwait(false);
        var second = await CreatePlayerAsync(game, "Second").ConfigureAwait(false);
        var seller = reversePlayers ? second : first;
        var buyer = reversePlayers ? first : second;
        seller.Player.Money = 100;
        buyer.Player.Money = 100;
        var item = new Persistence.BasicModel.Item
        {
            Id = Guid.NewGuid(), ItemSlot = 204, Durability = 1,
            Definition = new Persistence.BasicModel.ItemDefinition { Width = 1, Height = 1, Durability = 1 },
        };
        item.SetStorePrice(new ShopPrice(20));
        Assert.That(await seller.Player.ShopStorage!.AddItemAsync(204, item).ConfigureAwait(false), Is.True);
        seller.Player.ShopStorage.StoreOpen = true;
        var group = (StoreHandlerGroupPlugIn)seller.Player.MainPacketHandler[StoreHandlerGroupPlugIn.GroupKey]!;
        var gate = (GatedStoreHandler)group[GatedStoreHandler.SubCode]!;
        var ownerPacket = seller.Receive(new ReadOnlySequence<byte>(new byte[] { 0xC1, 4, 0x3F, GatedStoreHandler.SubCode })).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var ownerSave = seller.Player.SaveProgressAsync().AsTask();
        var packet = new byte[PlayerShopItemBuyRequest.Length];
        var request = new PlayerShopItemBuyRequest(packet);
        request.PlayerId = seller.Player.Id;
        request.PlayerName = seller.Player.SelectedCharacter!.Name!;
        request.ItemSlot = 204;
        var purchase = buyer.Receive(new ReadOnlySequence<byte>(packet)).AsTask();
        try
        {
            Assert.That(ownerSave.IsCompleted, Is.False, "Ordinary shop sub-handlers still serialize mutations with progress saves.");
            Assert.That(purchase.IsCompleted, Is.False, "The purchase waits for the seller before changing any value.");
            trade.Verify(c => c.SaveChangesAsync(It.IsAny<System.Threading.CancellationToken>()), Times.Never);
        }
        finally
        {
            gate.Release.TrySetResult();
            await Task.WhenAll(ownerPacket, ownerSave, purchase).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        Mock.Get(buyer.Player.ViewPlugIns.GetPlugIn<IPlayerShopBuyRequestResultPlugIn>()!)
            .Verify(v => v.ShowResultAsync(seller.Player, ItemBuyResult.Success, item), Times.Once);
        Assert.That(buyer.Player.Inventory!.Items.Any(i => ReferenceEquals(i, item)), Is.True);
        Assert.That(seller.Player.Money, Is.EqualTo(120));
        Assert.That(buyer.Player.Money, Is.EqualTo(80));
    }

    private static async ValueTask<(TestRemotePlayer Player, AsyncEventHandler<ReadOnlySequence<byte>> Receive)> CreatePlayerAsync(GameServerContext game, string name)
    {
        AsyncEventHandler<ReadOnlySequence<byte>>? receive = null;
        var connection = new Mock<IConnection>();
        connection.SetupAdd(c => c.PacketReceived += It.IsAny<AsyncEventHandler<ReadOnlySequence<byte>>>())
            .Callback<AsyncEventHandler<ReadOnlySequence<byte>>>(handler => receive = handler);
        var player = new TestRemotePlayer(game, connection.Object);
        var character = PlayerTestHelper.CreateCharacter(game);
        character.Name = name;
        await PlayerTestHelper.EnterWorldAsync(player, character).ConfigureAwait(false);
        return (player, receive!);
    }

    /// <summary>
    /// Pauses an ordinary shop sub-request while it owns the player's persistence scope.
    /// </summary>
    [BelongsToGroup(StoreHandlerGroupPlugIn.GroupKey)]
    public sealed class GatedStoreHandler : ISubPacketHandlerPlugIn
    {
        /// <summary>
        /// The otherwise unused subcode for this fixture.
        /// </summary>
        public const byte SubCode = 0xFC;

        /// <summary>
        /// Initializes a new instance of the <see cref="GatedStoreHandler"/> class.
        /// </summary>
        /// <param name="client">The client version provider makes this handler belong to its connection.</param>
        public GatedStoreHandler(IClientVersionProvider client)
        {
        }

        /// <inheritdoc />
        public byte Key => SubCode;

        /// <inheritdoc />
        public bool IsEncryptionExpected => false;

        /// <summary>
        /// Gets the signal that the sub-request owns its persistence scope.
        /// </summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Gets the signal which finishes the ordinary request.
        /// </summary>
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <inheritdoc />
        public async ValueTask HandlePacketAsync(Player player, Memory<byte> packet)
        {
            this.Entered.TrySetResult();
            await this.Release.Task.ConfigureAwait(false);
        }
    }

    private sealed class TestRemotePlayer(IGameServerContext game, IConnection connection)
        : RemotePlayer(game, connection, new ClientVersion(6, 3, ClientLanguage.Invariant))
    {
        protected override ICustomPlugInContainer<IViewPlugIn> CreateViewPlugInContainer() => new MockViewPlugInContainer();
    }
}
