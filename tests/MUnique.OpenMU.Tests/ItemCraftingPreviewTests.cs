// <copyright file="ItemCraftingPreviewTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Moq;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.ItemCrafting;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.GameLogic.Views.NPC;
using MUnique.OpenMU.GameServer.MessageHandler.Items;
using MUnique.OpenMU.Network.Packets.ClientToServer;
using B = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Tests for the preview of the success rate and price of a crafting, which is sent to the client
/// when the items of the crafting dialog change.
/// </summary>
[TestFixture]
public class ItemCraftingPreviewTests
{
    private const byte CraftingNumber = 42;

    private const byte SuccessRate = 40;

    private const int Price = 1000;

    /// <summary>
    /// Tests that moving the required items into the chaos machine shows the matching crafting
    /// with its success rate and price.
    /// </summary>
    [Test]
    public async ValueTask MovingTheRequiredItemIntoTheMachineShowsThePreviewAsync()
    {
        var (player, crafting, ingredient) = await PrepareAsync().ConfigureAwait(false);

        await new MoveItemAction().MoveItemAsync(player, ingredient.ItemSlot, Storages.Inventory, 0, Storages.ChaosMachine).ConfigureAwait(false);

        Mock.Get(player.ViewPlugIns.GetPlugIn<IShowItemCraftingPreviewPlugIn>()!)
            .Verify(p => p.ShowCraftingPreviewAsync(crafting, SuccessRate, Price), Times.Once);
    }

    /// <summary>
    /// Tests that moving the required item out of the chaos machine again shows that no crafting matches.
    /// </summary>
    [Test]
    public async ValueTask MovingTheRequiredItemOutOfTheMachineShowsNoCraftingAsync()
    {
        var (player, _, ingredient) = await PrepareAsync().ConfigureAwait(false);
        var moveAction = new MoveItemAction();
        var inventorySlot = ingredient.ItemSlot;
        await moveAction.MoveItemAsync(player, inventorySlot, Storages.Inventory, 0, Storages.ChaosMachine).ConfigureAwait(false);

        await moveAction.MoveItemAsync(player, 0, Storages.ChaosMachine, inventorySlot, Storages.Inventory).ConfigureAwait(false);

        Mock.Get(player.ViewPlugIns.GetPlugIn<IShowItemCraftingPreviewPlugIn>()!)
            .Verify(p => p.ShowCraftingPreviewAsync(null, 0, 0), Times.Once);
    }

    /// <summary>
    /// Tests that the crafting bonus of the player is included in the success rate of the preview.
    /// </summary>
    [Test]
    public async ValueTask PreviewIncludesTheCraftingBonusAsync()
    {
        var (player, crafting, ingredient) = await PrepareAsync().ConfigureAwait(false);
        player.Attributes!.AddElement(new SimpleElement(5, AggregateType.AddRaw), Stats.CraftingBonusPercentage);

        await new MoveItemAction().MoveItemAsync(player, ingredient.ItemSlot, Storages.Inventory, 0, Storages.ChaosMachine).ConfigureAwait(false);

        Mock.Get(player.ViewPlugIns.GetPlugIn<IShowItemCraftingPreviewPlugIn>()!)
            .Verify(p => p.ShowCraftingPreviewAsync(crafting, (byte)(SuccessRate + 5), Price), Times.Once);
    }

    /// <summary>
    /// Verifies that the recipe selected for the preview is submitted when several recipes share a request number.
    /// </summary>
    /// <param name="branchNumber">The branch item number.</param>
    /// <param name="amount">The number of branches in the stack.</param>
    /// <param name="sendLegacyRequest">Whether the client omits the recipe number.</param>
    /// <param name="expectSuccess">Whether the input forms a valid recipe.</param>
    [TestCase(88, 10, false, true)]
    [TestCase(89, 30, false, true)]
    [TestCase(90, 50, false, true)]
    [TestCase(89, 30, true, true)]
    [TestCase(88, 9, false, false)]
    [TestCase(89, 31, false, false)]
    [TestCase(90, 30, false, false)]
    [TestCase(89, 29, true, false)]
    public async ValueTask CherryBlossomVariantsUseTheMatchingRecipeAsync(
        short branchNumber, byte amount, bool sendLegacyRequest, bool expectSuccess)
    {
        const byte recipeNumber = 41;
        (short BranchNumber, byte Amount)[] recipes = [(88, 10), (89, 30), (90, 50)];
        var npc = new B.MonsterDefinition { Number = 450, NpcWindow = NpcWindow.CherryBlossomBranchesAssembly };
        Item? stack = null;
        ItemDefinition? expectedOutput = null;
        ItemCrafting? expectedCrafting = null;
        foreach (var (number, required) in recipes)
        {
            var branch = new B.ItemDefinition { Id = Guid.NewGuid(), Group = 14, Number = number, Width = 1, Height = 2, Durability = byte.MaxValue };
            var output = new B.ItemDefinition { Id = Guid.NewGuid(), Group = 12, Number = number, Width = 1, Height = 1, Durability = 1 };
            var ingredient = new B.ItemCraftingRequiredItem
            {
                MinimumAmount = required,
                MaximumAmount = required,
                MaximumItemLevel = byte.MaxValue,
                SuccessResult = MixResult.Disappear,
            };
            ingredient.PossibleItems.Add(branch);
            var settings = new B.SimpleCraftingSettings { SuccessPercent = 100, ResultItemSelect = ResultItemSelection.All };
            settings.RequiredItems.Add(ingredient);
            settings.ResultItems.Add(new B.ItemCraftingResultItem { ItemDefinition = output, Durability = 1 });
            var crafting = new B.ItemCrafting { Id = Guid.NewGuid(), Number = recipeNumber, SimpleCraftingSettings = settings };
            npc.ItemCraftings.Add(crafting);

            if (number == branchNumber)
            {
                stack = new B.Item { Id = Guid.NewGuid(), Definition = branch, Durability = amount };
                expectedOutput = output;
                expectedCrafting = crafting;
            }
        }

        Assert.That(stack, Is.Not.Null);
        await using var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        Assert.That(await player.Inventory!.AddItemAsync(stack!).ConfigureAwait(false), Is.True);
        await new TalkNpcAction().TalkToNpcAsync(player, new NonPlayerCharacter(null!, npc, null!)).ConfigureAwait(false);
        await new MoveItemAction().MoveItemAsync(player, stack!.ItemSlot, Storages.Inventory, 0, Storages.CherryBlossomSpirit).ConfigureAwait(false);
        Assert.That(player.TemporaryStorage!.Items.Single(), Is.SameAs(stack));
        Mock.Get(player.ViewPlugIns.GetPlugIn<IShowItemCraftingPreviewPlugIn>()!)
            .Verify(p => p.ShowCraftingPreviewAsync(expectSuccess ? expectedCrafting : null, expectSuccess ? (byte)100 : (byte)0, 0), Times.Once);
        var balance = player.Money;

        byte[] packet = sendLegacyRequest
            ? [0xC1, 3, ChaosMachineMixRequest.Code]
            : [0xC1, 5, ChaosMachineMixRequest.Code, recipeNumber, 0];
        await new ChaosMixHandlerPlugIn().HandlePacketAsync(player, packet).ConfigureAwait(false);

        var result = player.TemporaryStorage.Items.Single();
        var resultView = Mock.Get(player.ViewPlugIns.GetPlugIn<IShowItemCraftingResultPlugIn>()!);
        if (expectSuccess)
        {
            Assert.That(result, Is.Not.SameAs(stack));
            Assert.That(result.Definition, Is.SameAs(expectedOutput));
            resultView.Verify(p => p.ShowResultAsync(CraftingResult.Success, result), Times.Once);
        }
        else
        {
            Assert.That(result, Is.SameAs(stack));
            Assert.That(result.Durability, Is.EqualTo(amount));
            resultView.Verify(p => p.ShowResultAsync(sendLegacyRequest ? CraftingResult.IncorrectMixItems : CraftingResult.LackingMixItems, null), Times.Once);
        }

        Assert.That(player.Money, Is.EqualTo(balance));
    }

    private static NonPlayerCharacter CreateCraftingNpc(ItemCrafting crafting)
    {
        var definition = new Mock<MonsterDefinition>();
        definition.SetupAllProperties();
        definition.Setup(d => d.ItemCraftings).Returns(new List<ItemCrafting> { crafting });
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

    private static async ValueTask<(Player Player, ItemCrafting Crafting, Item Ingredient)> PrepareAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var ingredient = CreateItem();
        Assert.That(await player.Inventory!.AddItemAsync(ingredient).ConfigureAwait(false), Is.True);
        var crafting = new ItemCrafting
        {
            Number = CraftingNumber,
            ItemCraftingHandlerClassName = typeof(OneItemCraftingHandler).AssemblyQualifiedName!,
        };

        await new TalkNpcAction().TalkToNpcAsync(player, CreateCraftingNpc(crafting)).ConfigureAwait(false);
        return (player, crafting, ingredient);
    }

    /// <summary>
    /// A crafting which requires any item in the crafting dialog.
    /// </summary>
    private sealed class OneItemCraftingHandler : BaseItemCraftingHandler
    {
        /// <inheritdoc />
        public override CraftingResult? TryGetRequiredItems(Player player, out IList<CraftingRequiredItemLink> items, out byte successRateByItems)
        {
            var itemsInDialog = player.TemporaryStorage?.Items.ToList() ?? [];
            items = itemsInDialog.Count > 0
                ? [new CraftingRequiredItemLink(itemsInDialog, new ItemCraftingRequiredItem())]
                : [];
            successRateByItems = SuccessRate;
            return items.Count > 0 ? null : CraftingResult.IncorrectMixItems;
        }

        /// <inheritdoc />
        protected override int GetPrice(byte successRate, IList<CraftingRequiredItemLink> requiredItems)
        {
            // The price must be based on the success rate of the items, not on the one including the crafting bonus.
            return successRate == SuccessRate ? Price * requiredItems.Sum(link => link.Items.Count()) : 0;
        }

        /// <inheritdoc />
        protected override ValueTask<List<Item>> CreateOrModifyResultItemsAsync(IList<CraftingRequiredItemLink> requiredItems, Player player, byte socketSlot, byte successRate)
        {
            Assert.Fail($"A preview must not craft, but {player.SelectedCharacter?.Name} crafted {requiredItems.Count} items into socket slot {socketSlot} with a success rate of {successRate} %.");
            return ValueTask.FromResult(new List<Item>());
        }
    }
}
