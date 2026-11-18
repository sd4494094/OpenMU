// <copyright file="CraftingRecoveryTests.cs" company="MUnique">
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
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.GameLogic.Views.NPC;
using B = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Verifies ownership of current crafting results which cannot fit in the inventory on logout.
/// </summary>
[TestFixture]
public class CraftingRecoveryTests
{
    /// <summary>
    /// A larger result survives logout and character re-entry without restoring consumed inputs
    /// or charging again. Once space is freed, the same item returns exactly once.
    /// </summary>
    /// <param name="enoughRoom">Whether the initial result fits in the bag.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async ValueTask LargerCraftingResultSurvivesLogoutAndReturnsWhenSpaceIsFreedAsync(bool enoughRoom)
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var character = player.SelectedCharacter!;
        var inventory = player.Inventory!;
        var persisted = inventory.ItemStorage;
        var temporary = player.TemporaryStorage!;
        var ingredientDefinition = new B.ItemDefinition { Width = 1, Height = 1, Durability = 1 };
        var resultDefinition = new B.ItemDefinition { Width = 2, Height = 2, Durability = 1 };
        foreach (var slot in inventory.FreeSlots.ToArray())
        {
            Assert.That(await inventory.AddItemAsync(slot, CreateItem(player, ingredientDefinition)).ConfigureAwait(false), Is.True);
        }

        var ingredient = inventory.Items.Single(item => item.ItemSlot == 12);
        if (enoughRoom)
        {
            foreach (var slot in new byte[] { 13, 20, 21 })
            {
                await inventory.RemoveItemAsync(inventory.Items.Single(item => item.ItemSlot == slot)).ConfigureAwait(false);
            }
        }

        var settings = new B.SimpleCraftingSettings { SuccessPercent = 100, Money = 10, ResultItemSelect = ResultItemSelection.All };
        var requirement = new B.ItemCraftingRequiredItem { MinimumAmount = 1, MaximumAmount = 1, MaximumItemLevel = 15, SuccessResult = MixResult.Disappear };
        requirement.PossibleItems.Add(ingredientDefinition);
        settings.RequiredItems.Add(requirement);
        settings.ResultItems.Add(new B.ItemCraftingResultItem { ItemDefinition = resultDefinition, Durability = 1 });
        var npcDefinition = new B.MonsterDefinition { NpcWindow = NpcWindow.ChaosMachine };
        npcDefinition.ItemCraftings.Add(new B.ItemCrafting { Number = 1, SimpleCraftingSettings = settings });
        var npc = new NonPlayerCharacter(null!, npcDefinition, null!);
        persisted.Money = 1000;
        await new TalkNpcAction().TalkToNpcAsync(player, npc).ConfigureAwait(false);
        await inventory.RemoveItemAsync(ingredient).ConfigureAwait(false);
        Assert.That(await temporary.AddItemAsync(0, ingredient).ConfigureAwait(false), Is.True);
        var fillers = inventory.Items.Select(item => (Item: item, Slot: item.ItemSlot)).ToArray();
        var (status, result) = await new SimpleItemCraftingHandler(settings).DoMixAsync(player, 0).ConfigureAwait(false);
        Assert.That(status, Is.EqualTo(CraftingResult.Success));
        Assert.That(result, Is.Not.Null);
        Assert.That(player.BackupInventory, Is.Null);
        await new CloseNpcDialogAction().CloseNpcDialogAsync(player).ConfigureAwait(false);
        Assert.That(inventory.Items.Contains(result!), Is.EqualTo(enoughRoom));

        await player.RemoveFromGameAsync().ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(persisted.Items.Contains(result!), Is.EqualTo(enoughRoom));
            Assert.That(character.RecoveryStorage?.Items.Contains(result!) ?? false, Is.EqualTo(!enoughRoom));
            Assert.That(persisted.Items, Does.Not.Contain(ingredient));
            Assert.That(persisted.Money, Is.EqualTo(990));
            Assert.That(fillers.All(entry => persisted.Items.Contains(entry.Item) && entry.Item.ItemSlot == entry.Slot), Is.True);
        });

        await PlayerTestHelper.EnterWorldAsync(player, character).ConfigureAwait(false);
        Assert.That(player.Inventory!.Items.Contains(result!), Is.EqualTo(enoughRoom));
        if (!enoughRoom)
        {
            await new TalkNpcAction().TalkToNpcAsync(player, npc).ConfigureAwait(false);
            Assert.That(player.OpenedNpc, Is.Null, "A new mix waits for the retained result to be recovered.");
            Mock.Get(player.Account!).Setup(account => account.Vault).Returns(player.PersistenceContext.CreateNew<ItemStorage>());
            var vaultNpc = new NonPlayerCharacter(null!, new B.MonsterDefinition { NpcWindow = NpcWindow.VaultStorage }, null!);
            await new TalkNpcAction().TalkToNpcAsync(player, vaultNpc).ConfigureAwait(false);
            Assert.That(player.OpenedNpc, Is.SameAs(vaultNpc), "The vault remains accessible to free bag space.");
            await new CloseNpcDialogAction().CloseNpcDialogAsync(player).ConfigureAwait(false);
            foreach (var slot in new byte[] { 13, 20, 21 })
            {
                await player.Inventory.RemoveItemAsync(player.Inventory.Items.Single(item => item.ItemSlot == slot)).ConfigureAwait(false);
            }

            await new TalkNpcAction().TalkToNpcAsync(player, npc).ConfigureAwait(false);
            Assert.That(player.OpenedNpc, Is.SameAs(npc));
            Assert.That(character.RecoveryStorage!.Items, Is.Empty);
            await new CloseNpcDialogAction().CloseNpcDialogAsync(player).ConfigureAwait(false);
        }

        Assert.That(await player.RecoverInventoryItemsAsync().ConfigureAwait(false), Is.False);
        Assert.That(persisted.Items.Count(item => ReferenceEquals(item, result)), Is.EqualTo(1));
        await player.RemoveFromGameAsync().ConfigureAwait(false);
        Assert.That(persisted.Items.Count(item => ReferenceEquals(item, result)), Is.EqualTo(1));
        Assert.That(persisted.Money, Is.EqualTo(990));
        Assert.That(persisted.Items, Does.Not.Contain(ingredient));
    }

    /// <summary>
    /// Pending results are recovered by actual rectangle capacity; later items can fit while a
    /// larger earlier item stays owned, with no loss from retrying or repeated character entry.
    /// </summary>
    [Test]
    public async ValueTask RecoveryPreservesUnfittingItemsAcrossPartialRecoveryAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var character = player.SelectedCharacter!;
        var inventory = player.Inventory!;
        var oneCell = new B.ItemDefinition { Width = 1, Height = 1, Durability = 1 };
        foreach (var slot in inventory.FreeSlots.ToArray())
        {
            Assert.That(await inventory.AddItemAsync(slot, CreateItem(player, oneCell)).ConfigureAwait(false), Is.True);
        }

        var large = CreateItem(player, new B.ItemDefinition { Width = 2, Height = 2, Durability = 1 });
        var small = CreateItem(player, oneCell);
        character.RecoveryStorage = player.PersistenceContext.CreateNew<ItemStorage>();
        character.RecoveryStorage.Items.Add(large);
        character.RecoveryStorage.Items.Add(small);
        await inventory.RemoveItemAsync(inventory.Items.Single(item => item.ItemSlot == 12)).ConfigureAwait(false);
        Assert.That(await player.RecoverInventoryItemsAsync().ConfigureAwait(false), Is.True);
        Assert.That(character.RecoveryStorage.Items, Is.EqualTo(new[] { large }));
        Assert.That(inventory.Items, Does.Contain(small));
        Assert.That(await player.RecoverInventoryItemsAsync().ConfigureAwait(false), Is.False);
        await player.RemoveFromGameAsync().ConfigureAwait(false);
        await PlayerTestHelper.EnterWorldAsync(player, character).ConfigureAwait(false);
        Assert.That(character.RecoveryStorage.Items, Is.EqualTo(new[] { large }));
        Assert.That(player.Inventory!.Items.Count(item => ReferenceEquals(item, small)), Is.EqualTo(1));
    }

    private static Item CreateItem(Player player, ItemDefinition definition)
    {
        var item = player.PersistenceContext.CreateNew<Item>();
        item.Definition = definition;
        item.Durability = 1;
        return item;
    }
}
