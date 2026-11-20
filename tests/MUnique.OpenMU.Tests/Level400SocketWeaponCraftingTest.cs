// <copyright file="Level400SocketWeaponCraftingTest.cs" company="MUnique">
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
using MUnique.OpenMU.GameLogic.PlayerActions.Craftings;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.GameLogic.Views.NPC;
using MUnique.OpenMU.GameServer.MessageHandler.Items;
using MUnique.OpenMU.Network.Packets.ClientToServer;
using B = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Verifies the equipment conditions of the socket weapon mix through preview and submission.
/// </summary>
[TestFixture]
public class Level400SocketWeaponCraftingTest
{
    /// <summary>
    /// Tests that the equipment and catalysts determine the preview and settlement.
    /// </summary>
    /// <param name="gearLevel">The level of the third equipment item.</param>
    /// <param name="sockets">The number of sockets in the third equipment item.</param>
    /// <param name="optionLevel">The additional option level of the third equipment item.</param>
    /// <param name="fixedOption">Whether the third equipment item has a fixed option.</param>
    /// <param name="paperNumber">The recipe paper in the mix.</param>
    /// <param name="expectSuccess">Whether the input forms a complete recipe.</param>
    [TestCase(7, 1, 1, false, 191, true)]
    [TestCase(8, 3, 2, false, 192, true)]
    [TestCase(6, 1, 1, false, 191, false)]
    [TestCase(7, 0, 1, false, 191, false)]
    [TestCase(7, 1, 0, false, 191, false)]
    [TestCase(7, 1, 1, true, 191, false)]
    [TestCase(7, 1, 1, false, 193, false)]
    public async ValueTask MatchesEquipmentAndRecipePaperAsync(
        byte gearLevel, int sockets, int optionLevel, bool fixedOption, short paperNumber, bool expectSuccess)
    {
        const byte mixNumber = 57;
        const int price = 1_000_000;
        var gear = CreateDefinition(0, 27);
        (short Paper, B.ItemDefinition Material, B.ItemDefinition Secondary)[] recipes =
        [
            (191, CreateDefinition(14, 180), CreateDefinition(14, 188)),
            (192, CreateDefinition(14, 181), CreateDefinition(14, 185)),
        ];
        var optionType = new B.ItemOptionType { Id = ItemOptionTypes.Option.Id };
        var additionalOption = new B.IncreasableItemOption { Id = Guid.NewGuid(), OptionType = optionType };
        additionalOption.LevelDependentOptions.Add(new B.ItemOptionOfLevel { Level = 1 });
        var fixedAdditionalOption = new B.IncreasableItemOption { Id = Guid.NewGuid(), OptionType = optionType };
        var npc = new B.MonsterDefinition { Number = 238, NpcWindow = NpcWindow.ChaosMachine };
        var paper = CreateDefinition(14, paperNumber);
        ItemDefinition? expectedOutput = null;
        ItemCrafting? expectedCrafting = null;
        foreach (var (number, material, secondary) in recipes)
        {
            var equipment = Require(gear, 3);
            equipment.MinimumItemLevel = 7;
            equipment.RequiredItemOptions.Add(optionType);
            var settings = new B.SimpleCraftingSettings { Money = price, SuccessPercent = 100, ResultItemSelect = ResultItemSelection.All };
            settings.RequiredItems.Add(equipment);
            settings.RequiredItems.Add(Require(number == paperNumber ? paper : CreateDefinition(14, number), 1));
            settings.RequiredItems.Add(Require(material, 2));
            settings.RequiredItems.Add(Require(secondary, 1));
            var output = CreateDefinition(12, number);
            settings.ResultItems.Add(new B.ItemCraftingResultItem { ItemDefinition = output, Durability = 1 });
            var crafting = new B.ItemCrafting
            {
                Id = Guid.NewGuid(),
                Number = mixNumber,
                SimpleCraftingSettings = settings,
                ItemCraftingHandlerClassName = typeof(Level400SocketWeaponCrafting).FullName!,
            };
            npc.ItemCraftings.Add(crafting);
            if (number == paperNumber)
            {
                expectedCrafting = crafting;
                expectedOutput = output;
            }
        }

        var (_, firstDrop, secondDrop) = recipes[paperNumber == 192 ? 1 : 0];
        Item[] ingredients =
        [
            CreateEquipment(gear, 7, 1, additionalOption, 1),
            CreateEquipment(gear, 7, 2, additionalOption, 1),
            CreateEquipment(gear, gearLevel, sockets, fixedOption ? fixedAdditionalOption : additionalOption, optionLevel),
            CreateItem(paper), CreateItem(firstDrop), CreateItem(firstDrop), CreateItem(secondDrop),
        ];
        await using var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        player.Inventory!.ItemStorage.Money = 2 * price;
        await new TalkNpcAction().TalkToNpcAsync(player, new NonPlayerCharacter(null!, npc, null!)).ConfigureAwait(false);
        var move = new MoveItemAction();
        for (byte slot = 0; slot < ingredients.Length; slot++)
        {
            var item = ingredients[slot];
            Assert.That(await player.Inventory.AddItemAsync(item).ConfigureAwait(false), Is.True);
            await move.MoveItemAsync(player, item.ItemSlot, Storages.Inventory, slot, Storages.ChaosMachine).ConfigureAwait(false);
        }

        var temporaryStorage = player.TemporaryStorage!;
        Assert.That(temporaryStorage.Items, Is.EquivalentTo(ingredients));
        var preview = Mock.Get(player.ViewPlugIns.GetPlugIn<IShowItemCraftingPreviewPlugIn>()!);
        preview.Verify(p => p.ShowCraftingPreviewAsync(It.Is<ItemCrafting>(c => c != null), It.IsAny<byte>(), It.IsAny<int>()),
            expectSuccess ? Times.Once() : Times.Never());
        if (expectSuccess)
        {
            preview.Verify(p => p.ShowCraftingPreviewAsync(expectedCrafting, 100, price), Times.Once);
        }

        byte[] packet = [0xC1, 5, ChaosMachineMixRequest.Code, mixNumber, 0];
        await new ChaosMixHandlerPlugIn().HandlePacketAsync(player, packet).ConfigureAwait(false);
        var resultView = Mock.Get(player.ViewPlugIns.GetPlugIn<IShowItemCraftingResultPlugIn>()!);
        if (expectSuccess)
        {
            var result = temporaryStorage.Items.Single();
            Assert.That(result.Definition, Is.SameAs(expectedOutput));
            resultView.Verify(p => p.ShowResultAsync(CraftingResult.Success, result), Times.Once);
            Assert.That(player.Money, Is.EqualTo(price));
        }
        else
        {
            Assert.That(temporaryStorage.Items, Is.EquivalentTo(ingredients));
            resultView.Verify(p => p.ShowResultAsync(CraftingResult.LackingMixItems, null), Times.Once);
            Assert.That(player.Money, Is.EqualTo(2 * price));
        }
    }

    private static B.ItemDefinition CreateDefinition(byte group, short number) =>
        new() { Id = Guid.NewGuid(), Group = group, Number = number, Width = 1, Height = 1, Durability = 1 };

    private static B.ItemCraftingRequiredItem Require(B.ItemDefinition definition, byte amount)
    {
        var requirement = new B.ItemCraftingRequiredItem
        {
            MinimumAmount = amount,
            MaximumAmount = amount,
            MaximumItemLevel = byte.MaxValue,
            SuccessResult = MixResult.Disappear,
        };
        requirement.PossibleItems.Add(definition);
        return requirement;
    }

    private static B.Item CreateItem(B.ItemDefinition definition) =>
        new() { Id = Guid.NewGuid(), Definition = definition, Durability = 1 };

    private static Item CreateEquipment(B.ItemDefinition definition, byte level, int sockets, B.IncreasableItemOption option, int optionLevel)
    {
        var item = CreateItem(definition);
        item.Level = level;
        item.SocketCount = sockets;
        item.ItemOptions.Add(new B.ItemOptionLink { ItemOption = option, Level = optionLevel });
        return item;
    }
}
