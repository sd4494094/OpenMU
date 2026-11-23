// <copyright file="InventoryItemEquipTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.GameLogic.Views.Inventory;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Tests the state marker for an item which remains in the inventory.
/// </summary>
[TestFixture]
public class InventoryItemEquipTests
{
    private const byte Slot = 75;
    private const byte Group = 13;
    private const short Number = 134;

    /// <summary>
    /// A repeated request confirms the existing result without saving twice.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async ValueTask RepeatedRequestConfirmsSameSlotAsync()
    {
        var (player, context, item) = await CreateAsync().ConfigureAwait(false);
        await using var ownedPlayer = player;
        var action = new EquipInventoryItemAction();
        await action.EquipAsync(player, Slot, 254).ConfigureAwait(false);
        await action.EquipAsync(player, Slot, 254).ConfigureAwait(false);

        Assert.That(item.Durability, Is.EqualTo(254));
        Assert.That(player.Inventory!.GetItem(Slot), Is.SameAs(item));
        context.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        Mock.Get(player.ViewPlugIns.GetPlugIn<IInventoryItemEquipResultPlugIn>()!)
            .Verify(v => v.ShowInventoryItemEquipResultAsync(Slot, 254), Times.Exactly(2));

        await action.EquipAsync(player, Slot, 255).ConfigureAwait(false);

        Assert.That(item.Durability, Is.EqualTo(255));
        Assert.That(player.Inventory!.GetItem(Slot), Is.SameAs(item));
        context.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        Mock.Get(player.ViewPlugIns.GetPlugIn<IInventoryItemEquipResultPlugIn>()!)
            .Verify(v => v.ShowInventoryItemEquipResultAsync(Slot, 255), Times.Once);
    }

    /// <summary>
    /// A refused save retains and confirms the previous value.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async ValueTask RefusedSaveConfirmsPreviousStateAsync()
    {
        var (player, context, item) = await CreateAsync().ConfigureAwait(false);
        await using var ownedPlayer = player;
        context.SetupSequence(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<bool>(false))
            .Returns(new ValueTask<bool>(true));
        var action = new EquipInventoryItemAction();

        await action.EquipAsync(player, Slot, 254).ConfigureAwait(false);

        Assert.That(item.Durability, Is.EqualTo(255));
        var view = Mock.Get(player.ViewPlugIns.GetPlugIn<IInventoryItemEquipResultPlugIn>()!);
        view.Verify(v => v.ShowInventoryItemEquipResultAsync(Slot, 255), Times.Once);
        view.Verify(v => v.ShowInventoryItemEquipResultAsync(Slot, 254), Times.Never);

        await action.EquipAsync(player, Slot, 254).ConfigureAwait(false);

        Assert.That(item.Durability, Is.EqualTo(254));
        view.Verify(v => v.ShowInventoryItemEquipResultAsync(Slot, 254), Times.Once);
    }

    /// <summary>
    /// Rejects requests with an unknown slot, state or item definition.
    /// </summary>
    /// <param name="slot">The requested slot.</param>
    /// <param name="requested">The requested marker.</param>
    /// <param name="definitionNumber">The actual item's number.</param>
    /// <param name="durability">The actual item's durability.</param>
    /// <returns>The task.</returns>
    [TestCase(74, 254, Number, 255)]
    [TestCase(76, 254, Number, 255)]
    [TestCase(255, 254, Number, 255)]
    [TestCase(Slot, 253, Number, 255)]
    [TestCase(Slot, 254, 127, 255)]
    [TestCase(Slot, 254, Number, 2)]
    public async ValueTask InvalidRequestDoesNotSaveAsync(byte slot, byte requested, short definitionNumber, double durability)
    {
        var (player, context, item) = await CreateAsync(definitionNumber, durability).ConfigureAwait(false);
        await using var ownedPlayer = player;
        await new EquipInventoryItemAction().EquipAsync(player, slot, requested).ConfigureAwait(false);

        Assert.That(item.Durability, Is.EqualTo(durability));
        Assert.That(player.Inventory!.GetItem(Slot), Is.SameAs(item));
        context.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        Mock.Get(player.ViewPlugIns.GetPlugIn<IInventoryItemEquipResultPlugIn>()!)
            .Verify(v => v.ShowInventoryItemEquipResultAsync(It.IsAny<byte>(), It.IsAny<byte>()), Times.Never);
    }

    private static async ValueTask<(Player Player, Mock<IPlayerContext> Context, Item Item)> CreateAsync(short number = Number, double durability = 255)
    {
        var configuration = PlayerTestHelper.CreateGameConfiguration();
        var context = new Mock<IPlayerContext>();
        context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(true));
        var provider = new Mock<IPersistenceContextProvider>();
        provider.Setup(p => p.CreateNewPlayerContext(configuration)).Returns(context.Object);
        var maps = new MapInitializer(configuration, NullLogger<MapInitializer>.Instance, NullDropGenerator.Instance, null);
        var manager = new PlugInManager(null, NullLoggerFactory.Instance, null, null);
        var game = new GameContext(configuration, provider.Object, maps, NullLoggerFactory.Instance, manager, NullDropGenerator.Instance, new ConfigurationChangeMediator());
        manager.RegisterPlugIn<IFeaturePlugIn, InventoryItemEquipFeaturePlugIn>();
        manager.ActivatePlugIn<InventoryItemEquipFeaturePlugIn>();
        manager.ConfigurePlugIn(typeof(InventoryItemEquipFeaturePlugIn).GUID, new PlugInConfiguration
        {
            CustomConfiguration = """{"13":[134]}""",
        });
        maps.PlugInManager = manager;
        maps.PathFinderPool = game.PathFinderPool;
        var player = await PlayerTestHelper.CreatePlayerAsync(game).ConfigureAwait(false);
        var item = new Item
        {
            Definition = new ItemDefinition { Group = Group, Number = number, Width = 1, Height = 1 },
            Durability = durability,
        };
        Assert.That(await player.Inventory!.AddItemAsync(Slot, item).ConfigureAwait(false), Is.True);
        return (player, context, item);
    }
}
