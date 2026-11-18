// <copyright file="InventoryRequestTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.GameLogic.Views.Inventory;
using MUnique.OpenMU.GameServer;
using MUnique.OpenMU.GameServer.MessageHandler;
using MUnique.OpenMU.GameServer.RemoteView;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Network;
using MUnique.OpenMU.Network.Packets.ClientToServer;
using MUnique.OpenMU.Network.PlugIns;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;
using B = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Exercises inventory refresh requests through the real main and character packet dispatchers.
/// </summary>
[TestFixture]
public class InventoryRequestTests
{
    /// <summary>
    /// An ordinary player, including a seller with an open store, can request a read-only refresh.
    /// </summary>
    /// <param name="season">The client version to dispatch for.</param>
    /// <param name="storeOpen">Whether the player has an open personal store.</param>
    [TestCase(6, false)]
    [TestCase(6, true)]
    [TestCase(104, false)]
    [TestCase(104, true)]
    public async ValueTask RefreshesCurrentInventoryWithoutChangingOwnershipAsync(int season, bool storeOpen)
    {
        await using var game = CreateGame();
        await using var player = await EnterAsync(game, season).ConfigureAwait(false);
        var item = player.PersistenceContext.CreateNew<Item>();
        item.Definition = new B.ItemDefinition { Width = 1, Height = 1, Durability = 1 };
        Assert.That(await player.Inventory!.AddItemAsync(12, item).ConfigureAwait(false), Is.True);
        var sale = player.PersistenceContext.CreateNew<Item>();
        sale.Definition = item.Definition;
        Assert.That(await player.ShopStorage!.AddItemAsync(InventoryConstants.FirstStoreItemSlotIndex, sale).ConfigureAwait(false), Is.True);
        player.ShopStorage!.StoreOpen = storeOpen;
        player.Inventory.ItemStorage.Money = 1000;
        var expected = player.SelectedCharacter!.Inventory!.Items.ToArray();
        var view = Mock.Get(player.ViewPlugIns.GetPlugIn<IUpdateInventoryListPlugIn>()!);
        view.Invocations.Clear();

        await player.MainPacketHandler.HandlePacketAsync(player, Request()).ConfigureAwait(false);

        view.Verify(v => v.UpdateInventoryListAsync(), Times.Once);
        Assert.Multiple(() =>
        {
            Assert.That(player.SelectedCharacter.Inventory.Items, Is.EqualTo(expected));
            Assert.That(player.Inventory.Items, Does.Contain(item));
            Assert.That(player.ShopStorage.Items, Does.Contain(sale));
            Assert.That(item.ItemSlot, Is.EqualTo(12));
            Assert.That(sale.ItemSlot, Is.EqualTo(InventoryConstants.FirstStoreItemSlotIndex));
            Assert.That(player.ShopStorage.StoreOpen, Is.EqualTo(storeOpen));
            Assert.That(player.Money, Is.EqualTo(1000));
            Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.EnteredWorld));
        });
    }

    /// <summary>
    /// Unauthenticated clients have no inventory to refresh.
    /// </summary>
    [Test]
    public async ValueTask IgnoresRequestBeforeCharacterEntryAsync()
    {
        await using var game = CreateGame();
        await using var player = new RefreshPlayer(game, new Mock<IConnection>().Object, 6);
        var view = Mock.Get(player.ViewPlugIns.GetPlugIn<IUpdateInventoryListPlugIn>()!);

        await player.MainPacketHandler.HandlePacketAsync(player, Request()).ConfigureAwait(false);

        view.Verify(v => v.UpdateInventoryListAsync(), Times.Never);
        Assert.That(player.SelectedCharacter, Is.Null);
    }

    /// <summary>
    /// Refresh requests must not replace the client inventory while items are in a temporary dialog.
    /// </summary>
    /// <param name="npcDialog">Whether to use an NPC window rather than a trade.</param>
    [TestCase(true)]
    [TestCase(false)]
    public async ValueTask IgnoresRequestsDuringTemporaryDialogsAsync(bool npcDialog)
    {
        await using var game = CreateGame();
        await using var player = await EnterAsync(game, 6).ConfigureAwait(false);
        if (!npcDialog)
        {
            Assert.That(await player.PlayerState.TryAdvanceToAsync(PlayerState.TradeRequested).ConfigureAwait(false), Is.True);
        }

        var state = npcDialog ? PlayerState.NpcDialogOpened : PlayerState.TradeOpened;
        Assert.That(await player.PlayerState.TryAdvanceToAsync(state).ConfigureAwait(false), Is.True);
        var item = player.PersistenceContext.CreateNew<Item>();
        item.Definition = new B.ItemDefinition { Width = 1, Height = 1, Durability = 1 };
        Assert.That(await player.TemporaryStorage!.AddItemAsync(0, item).ConfigureAwait(false), Is.True);
        var view = Mock.Get(player.ViewPlugIns.GetPlugIn<IUpdateInventoryListPlugIn>()!);
        view.Invocations.Clear();

        await player.MainPacketHandler.HandlePacketAsync(player, Request()).ConfigureAwait(false);

        view.Verify(v => v.UpdateInventoryListAsync(), Times.Never);
        Assert.That(player.PlayerState.CurrentState, Is.EqualTo(state));
        Assert.That(player.TemporaryStorage.Items, Is.EqualTo(new[] { item }));
    }

    /// <summary>
    /// The XML specifies an encrypted, fixed-length request.
    /// </summary>
    /// <param name="header">The header to send through the dispatcher.</param>
    /// <param name="extraBytes">Extra bytes which make the fixed-length request invalid.</param>
    [TestCase(0xC1, 0)]
    [TestCase(0xC3, 1)]
    public async ValueTask IgnoresUnencryptedOrOverlongRequestsAsync(int header, int extraBytes)
    {
        await using var game = CreateGame();
        await using var player = await EnterAsync(game, 6).ConfigureAwait(false);
        var view = Mock.Get(player.ViewPlugIns.GetPlugIn<IUpdateInventoryListPlugIn>()!);
        view.Invocations.Clear();
        var packet = Request(extraBytes);
        packet.Span[0] = (byte)header;

        await player.MainPacketHandler.HandlePacketAsync(player, packet).ConfigureAwait(false);

        view.Verify(v => v.UpdateInventoryListAsync(), Times.Never);
    }

    private static Memory<byte> Request(int extraBytes = 0)
    {
        var packet = new Memory<byte>(new byte[InventoryRequest.Length + extraBytes]);
        _ = new InventoryRequest(packet);
        return packet;
    }

    private static GameServerContext CreateGame()
    {
        var configuration = PlayerTestHelper.CreateGameConfiguration();
        var manager = new PlugInManager(null, NullLoggerFactory.Instance, null, null);
        manager.DiscoverAndRegisterPlugIns(typeof(MainPacketHandlerPlugInContainer).Assembly);
        var initializer = new MapInitializer(configuration, NullLogger<MapInitializer>.Instance, NullDropGenerator.Instance, null);
        var game = new GameServerContext(
            new GameServerDefinition { GameConfiguration = configuration, ServerConfiguration = new GameServerConfiguration() },
            new Mock<IGuildServer>().Object, new Mock<IEventPublisher>().Object,
            new Mock<ILoginServer>().Object, new Mock<IFriendServer>().Object,
            new InMemoryPersistenceContextProvider(), initializer, NullLoggerFactory.Instance,
            manager, NullDropGenerator.Instance, new ConfigurationChangeMediator());
        game.StopPeriodicTasks();
        initializer.PlugInManager = manager;
        initializer.PathFinderPool = game.PathFinderPool;
        return game;
    }

    private static async ValueTask<RefreshPlayer> EnterAsync(GameServerContext game, int season)
    {
        var player = new RefreshPlayer(game, new Mock<IConnection>().Object, season);
        await PlayerTestHelper.EnterWorldAsync(player, PlayerTestHelper.CreateCharacter(game)).ConfigureAwait(false);
        for (var ready = 0; ready < 3 && player.PlayerState.CurrentState == PlayerState.ChangingMap; ready++)
        {
            await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
        }

        Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.EnteredWorld));
        return player;
    }

    private sealed class RefreshPlayer(IGameServerContext game, IConnection connection, int season)
        : RemotePlayer(game, connection, new ClientVersion((byte)season, 3, ClientLanguage.English))
    {
        protected override ICustomPlugInContainer<IViewPlugIn> CreateViewPlugInContainer() => new MockViewPlugInContainer();
    }
}
