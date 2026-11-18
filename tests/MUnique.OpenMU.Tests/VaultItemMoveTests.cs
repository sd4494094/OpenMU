// <copyright file="VaultItemMoveTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Moq;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.GameLogic.Views.Inventory;

/// <summary>
/// Tests vault page boundaries independently of client placement validation.
/// </summary>
[TestFixture]
public class VaultItemMoveTests
{
    /// <summary>
    /// A rejected deposit or rearrangement leaves both persistent collections and the source unchanged.
    /// </summary>
    /// <param name="targetSlot">The requested slot.</param>
    /// <param name="height">The item height.</param>
    /// <param name="fromVault">Whether the item is already in the vault.</param>
    [TestCase(112, 2, false)]
    [TestCase(112, 2, true)]
    [TestCase(104, 3, false)]
    [TestCase(104, 3, true)]
    [TestCase(127, 2, false)]
    [TestCase(232, 2, false)]
    [TestCase(240, 1, false)]
    [TestCase(255, 1, false)]
    public async ValueTask InvalidPlacementPreservesOriginalItemAsync(byte targetSlot, byte height, bool fromVault)
    {
        var player = await CreatePlayerAsync(true).ConfigureAwait(false);
        var source = fromVault ? player.Vault! : player.Inventory!;
        byte sourceSlot = fromVault ? (byte)40 : (byte)20;
        var item = CreateItem(height);
        Assert.That(await source.AddItemAsync(sourceSlot, item).ConfigureAwait(false), Is.True);
        var inventoryBefore = player.Inventory!.ItemStorage.Items.ToArray();
        var vaultBefore = player.Vault!.ItemStorage.Items.ToArray();

        await new MoveItemAction().MoveItemAsync(player, sourceSlot, fromVault ? Storages.Vault : Storages.Inventory, targetSlot, Storages.Vault).ConfigureAwait(false);

        Assert.That(source.GetItem(sourceSlot), Is.SameAs(item));
        Assert.That(item.ItemSlot, Is.EqualTo(sourceSlot));
        Assert.That(player.Inventory.ItemStorage.Items, Is.EqualTo(inventoryBefore));
        Assert.That(player.Vault.ItemStorage.Items, Is.EqualTo(vaultBefore));
        Mock.Get(player.ViewPlugIns.GetPlugIn<IItemMoveFailedPlugIn>()!).Verify(p => p.ItemMoveFailedAsync(item), Times.Once);
        Mock.Get(player.ViewPlugIns.GetPlugIn<IItemMovedPlugIn>()!).Verify(p => p.ItemMovedAsync(It.IsAny<Item>(), It.IsAny<byte>(), It.IsAny<Storages>()), Times.Never);
    }

    /// <summary>
    /// Legal placements at page edges can be deposited, reloaded and withdrawn without replacing the item.
    /// </summary>
    /// <param name="targetSlot">The requested slot.</param>
    /// <param name="height">The item height.</param>
    [TestCase(110, 2)]
    [TestCase(112, 1)]
    [TestCase(120, 2)]
    [TestCase(230, 2)]
    public async ValueTask LegalPagePlacementSurvivesReopenAndWithdrawalAsync(byte targetSlot, byte height)
    {
        var player = await CreatePlayerAsync(true).ConfigureAwait(false);
        var item = CreateItem(height);
        Assert.That(await player.Inventory!.AddItemAsync(20, item).ConfigureAwait(false), Is.True);
        var action = new MoveItemAction();

        await action.MoveItemAsync(player, 20, Storages.Inventory, targetSlot, Storages.Vault).ConfigureAwait(false);

        Assert.That(player.Inventory.GetItem(20), Is.Null);
        Assert.That(player.Vault!.GetItem(targetSlot), Is.SameAs(item));
        player.Vault = new Storage(InventoryConstants.WarehouseSize * 2, player.Vault.ItemStorage);
        Assert.That(player.Vault.GetItem(targetSlot), Is.SameAs(item));

        await action.MoveItemAsync(player, targetSlot, Storages.Vault, 20, Storages.Inventory).ConfigureAwait(false);

        Assert.That(player.Inventory.GetItem(20), Is.SameAs(item));
        Assert.That(player.Vault.Items, Is.Empty);
        Assert.That(player.Inventory.ItemStorage.Items.Single(), Is.SameAs(item));
        Mock.Get(player.ViewPlugIns.GetPlugIn<IItemMoveFailedPlugIn>()!).Verify(p => p.ItemMoveFailedAsync(It.IsAny<Item>()), Times.Never);
    }

    /// <summary>
    /// Existing items which an older server stored across a page boundary remain recoverable.
    /// </summary>
    [Test]
    public async ValueTask LegacyCrossPageItemCanStillBeWithdrawnAsync()
    {
        var player = await CreatePlayerAsync(true).ConfigureAwait(false);
        var item = CreateItem(2);
        Assert.That(await player.Vault!.AddItemAsync(112, item).ConfigureAwait(false), Is.True);
        player.Vault = new Storage(InventoryConstants.WarehouseSize * 2, player.Vault.ItemStorage);

        await new MoveItemAction().MoveItemAsync(player, 112, Storages.Vault, 20, Storages.Inventory).ConfigureAwait(false);

        Assert.That(player.Inventory!.GetItem(20), Is.SameAs(item));
        Assert.That(player.Vault.Items, Is.Empty);
        Assert.That(player.Vault.ItemStorage.Items, Is.Empty);
    }

    /// <summary>
    /// An account without the extension cannot deposit into the second page.
    /// </summary>
    [Test]
    public async ValueTask UnextendedVaultRejectsSecondPageAsync()
    {
        var player = await CreatePlayerAsync(false).ConfigureAwait(false);
        var item = CreateItem(1);
        Assert.That(await player.Inventory!.AddItemAsync(20, item).ConfigureAwait(false), Is.True);

        await new MoveItemAction().MoveItemAsync(player, 20, Storages.Inventory, 120, Storages.Vault).ConfigureAwait(false);

        Assert.That(player.Inventory.GetItem(20), Is.SameAs(item));
        Assert.That(player.Vault!.Items, Is.Empty);
        Mock.Get(player.ViewPlugIns.GetPlugIn<IItemMoveFailedPlugIn>()!).Verify(p => p.ItemMoveFailedAsync(item), Times.Once);
    }

    private static Item CreateItem(byte height)
    {
        var definition = new ItemDefinition { Width = 2, Height = height, IsStorable = true };
        return new Item { Definition = definition, Durability = 30 };
    }

    private static async ValueTask<Player> CreatePlayerAsync(bool extended)
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        player.Account!.IsVaultExtended = extended;
        var backing = new Mock<ItemStorage>();
        backing.Setup(s => s.Items).Returns(new List<Item>());
        player.Vault = new Storage(InventoryConstants.WarehouseSize * (extended ? 2 : 1), backing.Object);
        player.OpenedNpc = new NonPlayerCharacter(null!, new MonsterDefinition { NpcWindow = NpcWindow.VaultStorage }, null!);
        Assert.That(await player.PlayerState.TryAdvanceToAsync(PlayerState.NpcDialogOpened).ConfigureAwait(false), Is.True);
        return player;
    }
}
