// <copyright file="CraftingDialogBackupInventoryTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.ItemCrafting;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameLogic.Views.Inventory;
using MUnique.OpenMU.GameLogic.Views.NPC;

/// <summary>
/// Tests for the backup of the inventory which is taken when a crafting NPC (Chaos Goblin, Pet
/// Trainer, ...) is opened, and restored when the player disconnects before the mix.
/// </summary>
[TestFixture]
public class CraftingDialogBackupInventoryTests
{
    private const int GainedMoney = 1000;

    /// <summary>
    /// Verifies that what the player gains after closing the crafting dialog without a mix is kept
    /// on a disconnect, rather than rolled back to when the dialog was opened.
    /// </summary>
    [Test]
    public async ValueTask GainsAfterClosingTheCraftingDialogSurviveADisconnectAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var inventory = InventoryOf(player).ItemStorage;
        await new TalkNpcAction().TalkToNpcAsync(player, CreateCraftingNpc()).ConfigureAwait(false);
        await new CloseNpcDialogAction().CloseNpcDialogAsync(player).ConfigureAwait(false);

        var loot = CreateItem();
        Assert.That(await InventoryOf(player).AddItemAsync(loot).ConfigureAwait(false), Is.True);
        inventory.Money = GainedMoney;

        await player.RemoveFromGameAsync().ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(inventory.Items, Does.Contain(loot));
            Assert.That(inventory.Money, Is.EqualTo(GainedMoney));
        });
    }

    /// <summary>
    /// Verifies that an item left in the machine when its dialog was closed is returned to the
    /// inventory on a disconnect.
    /// </summary>
    [Test]
    public async ValueTask ItemLeftInTheMachineIsReturnedOnADisconnectAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var inventory = InventoryOf(player).ItemStorage;
        var ingredient = CreateItem();
        Assert.That(await InventoryOf(player).AddItemAsync(ingredient).ConfigureAwait(false), Is.True);
        await new TalkNpcAction().TalkToNpcAsync(player, CreateCraftingNpc()).ConfigureAwait(false);
        await PutIntoTheMachineAsync(player, ingredient).ConfigureAwait(false);
        Mock.Get(player.ViewPlugIns.GetPlugIn<IUpdateInventoryListPlugIn>()!).Invocations.Clear();
        await new CloseNpcDialogAction().CloseNpcDialogAsync(player).ConfigureAwait(false);

        Assert.That(inventory.Items, Does.Contain(ingredient));
        Assert.That(TemporaryStorageOf(player).Items, Is.Empty);
        Mock.Get(player.ViewPlugIns.GetPlugIn<IUpdateInventoryListPlugIn>()!)
            .Verify(p => p.UpdateInventoryListAsync(), Times.Once);

        await player.RemoveFromGameAsync().ConfigureAwait(false);

        Assert.That(inventory.Items.Count(item => ReferenceEquals(item, ingredient)), Is.EqualTo(1));
    }

    /// <summary>
    /// Closing cannot leave temporary items behind while permitting the freed bag cells to be reused.
    /// </summary>
    /// <param name="freeSlots">The bag cells available for two temporary items.</param>
    [TestCase(0)]
    [TestCase(1)]
    public async ValueTask FullInventoryKeepsTheCraftingDialogAndItemsUntilTheyCanReturnAsync(int freeSlots)
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var inventory = InventoryOf(player);
        var ingredients = new[] { CreateItem(), CreateItem() };
        foreach (var ingredient in ingredients)
        {
            Assert.That(await inventory.AddItemAsync(ingredient).ConfigureAwait(false), Is.True);
        }

        var npc = CreateCraftingNpc();
        await new TalkNpcAction().TalkToNpcAsync(player, npc).ConfigureAwait(false);
        var backup = player.BackupInventory;
        for (byte slot = 0; slot < ingredients.Length; slot++)
        {
            await inventory.RemoveItemAsync(ingredients[slot]).ConfigureAwait(false);
            Assert.That(await TemporaryStorageOf(player).AddItemAsync(slot, ingredients[slot]).ConfigureAwait(false), Is.True);
        }

        foreach (var slot in inventory.FreeSlots.ToArray().Skip(freeSlots))
        {
            Assert.That(await inventory.AddItemAsync(slot, CreateItem()).ConfigureAwait(false), Is.True);
        }

        var initial = inventory.Items.Select(item => (Item: item, Slot: item.ItemSlot)).ToArray();
        inventory.ItemStorage.Money = GainedMoney;
        await new CloseNpcDialogAction().CloseNpcDialogAsync(player).ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.NpcDialogOpened));
            Assert.That(player.OpenedNpc, Is.SameAs(npc));
            Assert.That(player.BackupInventory, Is.SameAs(backup));
            Assert.That(inventory.Items, Is.EquivalentTo(initial.Select(x => x.Item)));
            Assert.That(initial.All(x => x.Item.ItemSlot == x.Slot), Is.True);
            Assert.That(TemporaryStorageOf(player).Items, Is.EquivalentTo(ingredients));
            Assert.That(ingredients.Select(item => item.ItemSlot), Is.EqualTo(new byte[] { 0, 1 }));
            Assert.That(inventory.ItemStorage.Money, Is.EqualTo(GainedMoney));
        });
        Mock.Get(player.ViewPlugIns.GetPlugIn<INpcDialogClosedPlugIn>()!)
            .Verify(p => p.DialogClosedAsync(It.IsAny<MonsterDefinition>()), Times.Never);

        foreach (var item in inventory.Items.Take(ingredients.Length - freeSlots).ToArray())
        {
            await inventory.RemoveItemAsync(item).ConfigureAwait(false);
        }

        await new CloseNpcDialogAction().CloseNpcDialogAsync(player).ConfigureAwait(false);
        Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.EnteredWorld));
        Assert.That(player.OpenedNpc, Is.Null);
        Assert.That(player.BackupInventory, Is.Null);
        Assert.That(TemporaryStorageOf(player).Items, Is.Empty);
        var final = inventory.Items.Select(item => (Item: item, Slot: item.ItemSlot)).ToArray();
        await player.RemoveFromGameAsync().ConfigureAwait(false);
        Assert.That(inventory.ItemStorage.Items, Is.EquivalentTo(final.Select(x => x.Item)));
        Assert.That(final.All(x => x.Item.ItemSlot == x.Slot), Is.True);
        Assert.That(inventory.ItemStorage.Money, Is.EqualTo(GainedMoney));
    }

    /// <summary>
    /// Closing returns the current mix result and preserves the paid money after the old backup is discarded.
    /// </summary>
    [Test]
    public async ValueTask ClosingReturnsTheCurrentResultWithoutRestoringTheOldBackupAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var inventory = InventoryOf(player);
        await new TalkNpcAction().TalkToNpcAsync(player, CreateCraftingNpc()).ConfigureAwait(false);
        player.BackupInventory = null;
        inventory.ItemStorage.Money = GainedMoney;
        var result = CreateItem();
        Assert.That(await TemporaryStorageOf(player).AddItemAsync(0, result).ConfigureAwait(false), Is.True);

        await new CloseNpcDialogAction().CloseNpcDialogAsync(player).ConfigureAwait(false);

        Assert.That(TemporaryStorageOf(player).Items, Is.Empty);
        Assert.That(inventory.Items, Is.EquivalentTo(new[] { result }));
        Assert.That(player.BackupInventory, Is.Null);
        await player.RemoveFromGameAsync().ConfigureAwait(false);
        Assert.That(inventory.ItemStorage.Items, Is.EquivalentTo(new[] { result }));
        Assert.That(inventory.ItemStorage.Money, Is.EqualTo(GainedMoney));
    }

    /// <summary>
    /// A close request outside an NPC dialog does not move items from another temporary-storage session.
    /// </summary>
    [Test]
    public async ValueTask ClosingOutsideTheDialogDoesNotMoveTemporaryItemsAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var item = CreateItem();
        Assert.That(await TemporaryStorageOf(player).AddItemAsync(0, item).ConfigureAwait(false), Is.True);

        await new CloseNpcDialogAction().CloseNpcDialogAsync(player).ConfigureAwait(false);

        Assert.That(TemporaryStorageOf(player).Items, Is.EquivalentTo(new[] { item }));
        Assert.That(InventoryOf(player).Items, Is.Empty);
        Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.EnteredWorld));
    }

    /// <summary>
    /// Verifies that a disconnect while the crafting dialog is still open restores the backup, which
    /// puts the item in the machine back into its inventory slot.
    /// </summary>
    [Test]
    public async ValueTask DisconnectWhileTheCraftingDialogIsOpenRestoresTheBackupAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var inventory = InventoryOf(player).ItemStorage;
        var ingredient = CreateItem();
        Assert.That(await InventoryOf(player).AddItemAsync(ingredient).ConfigureAwait(false), Is.True);
        var ingredientSlot = ingredient.ItemSlot;
        await new TalkNpcAction().TalkToNpcAsync(player, CreateCraftingNpc()).ConfigureAwait(false);
        await PutIntoTheMachineAsync(player, ingredient).ConfigureAwait(false);
        Assert.That(player.BackupInventory, Is.Not.Null);

        await player.RemoveFromGameAsync().ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(inventory.Items.Count(item => ReferenceEquals(item, ingredient)), Is.EqualTo(1));
            Assert.That(ingredient.ItemSlot, Is.EqualTo(ingredientSlot));
            Assert.That(player.BackupInventory, Is.Null);
        });
    }

    private static async ValueTask PutIntoTheMachineAsync(Player player, Item item)
    {
        await InventoryOf(player).RemoveItemAsync(item).ConfigureAwait(false);
        Assert.That(await TemporaryStorageOf(player).AddItemAsync(0, item).ConfigureAwait(false), Is.True);
    }

    private static IInventoryStorage InventoryOf(Player player)
    {
        return player.Inventory ?? throw new AssertionException("The player has no inventory.");
    }

    private static IStorage TemporaryStorageOf(Player player)
    {
        return player.TemporaryStorage ?? throw new AssertionException("The player has no temporary storage.");
    }

    private static NonPlayerCharacter CreateCraftingNpc()
    {
        var definition = new Mock<MonsterDefinition>();
        definition.SetupAllProperties();
        definition.Setup(d => d.ItemCraftings).Returns(new List<ItemCrafting> { new() });
        definition.Object.NpcWindow = NpcWindow.ChaosMachine;
        return new NonPlayerCharacter(null!, definition.Object, null!);
    }

    private static Item CreateItem()
    {
        var item = new Mock<Item>();
        item.SetupAllProperties();
        item.Setup(i => i.ItemOptions).Returns(new List<ItemOptionLink>());
        item.Setup(i => i.ItemSetGroups).Returns(new List<ItemOfItemSet>());
        item.Object.Definition = new ItemDefinition { Width = 1, Height = 1, Durability = 1 };
        item.Object.Durability = 1;
        return item.Object;
    }
}
