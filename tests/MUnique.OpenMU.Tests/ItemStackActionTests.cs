// <copyright file="ItemStackActionTests.cs" company="MUnique">
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

/// <summary>
/// Verifies exact jewel combining through the real player action and inventory.
/// </summary>
[TestFixture]
public class ItemStackActionTests
{
    /// <summary>
    /// Verifies singles, bundles and mixed materials conserve jewels for every configured mix number.
    /// </summary>
    /// <param name="target">The requested bundle size.</param>
    /// <param name="materials">The size of each material item.</param>
    [TestCase(10, new[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 })]
    [TestCase(20, new[] { 10, 10 })]
    [TestCase(30, new[] { 10, 10, 10 })]
    [TestCase(30, new[] { 20, 10 })]
    [TestCase(20, new[] { 10, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 })]
    [TestCase(30, new[] { 20, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 })]
    [TestCase(30, new[] { 10, 10, 10, 10 })]
    [TestCase(30, new[] { 20, 20, 10 })]
    [TestCase(30, new[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 })]
    public async ValueTask ExactMaterialsCombineForAllJewelTypesAsync(byte target, int[] materials)
    {
        for (byte number = 0; number < 10; number++)
        {
            var (player, mix) = await CreatePlayerAsync(number).ConfigureAwait(false);
            await AddMaterialsAsync(player, mix, materials).ConfigureAwait(false);
            var before = CountJewels(player, mix);

            await new ItemStackAction().StackItemsAsync(player, number, target).ConfigureAwait(false);

            Assert.That(player.Money, Is.EqualTo(10_000_000 - (target / 10 * 500_000)));
            Assert.That(CountJewels(player, mix), Is.EqualTo(before));
            Assert.That(player.Inventory!.Items.Count(item => item.Definition == mix.MixedJewel && (item.Level + 1) * 10 == target), Is.EqualTo(1));
            Assert.That(player.Inventory.Items.Count(), Is.EqualTo(materials.Sum() == target ? 1 : 2));

            var character = player.SelectedCharacter!;
            await player.RemoveFromGameAsync().ConfigureAwait(false);
            await player.SetSelectedCharacterAsync(character).ConfigureAwait(false);
            Assert.That(CountJewels(player, mix), Is.EqualTo(before));
            Assert.That(player.Money, Is.EqualTo(10_000_000 - (target / 10 * 500_000)));
        }
    }

    /// <summary>
    /// Verifies insufficient, oversized, same-size and malformed requests leave materials and money intact.
    /// </summary>
    /// <param name="target">The requested bundle size.</param>
    /// <param name="materials">The available materials.</param>
    [TestCase(30, new[] { 20, 20 })]
    [TestCase(20, new[] { 30 })]
    [TestCase(30, new[] { 30 })]
    [TestCase(20, new[] { 20 })]
    [TestCase(10, new[] { 10 })]
    [TestCase(30, new[] { 10, 10, 1 })]
    [TestCase(30, new[] { 40 })]
    [TestCase(0, new[] { 10, 10 })]
    [TestCase(15, new[] { 10, 1, 1, 1, 1, 1 })]
    [TestCase(40, new[] { 20, 20 })]
    public async ValueTask RejectedRequestsKeepInventoryAndMoneyAsync(byte target, int[] materials)
    {
        var (player, mix) = await CreatePlayerAsync(1).ConfigureAwait(false);
        await AddMaterialsAsync(player, mix, materials).ConfigureAwait(false);
        var before = player.Inventory!.Items.ToArray();

        await new ItemStackAction().StackItemsAsync(player, mix.Number, target).ConfigureAwait(false);

        Assert.That(player.Inventory.Items, Is.EquivalentTo(before));
        Assert.That(player.Money, Is.EqualTo(10_000_000));
    }

    /// <summary>
    /// Verifies another jewel type cannot fill a missing bundle.
    /// </summary>
    [Test]
    public async ValueTask OtherJewelTypesAreNotConsumedAsync()
    {
        var (player, mix) = await CreatePlayerAsync(1).ConfigureAwait(false);
        await AddMaterialsAsync(player, mix, [10, 10]).ConfigureAwait(false);
        var other = new JewelMix { SingleJewel = CreateDefinition(), MixedJewel = CreateDefinition() };
        await AddMaterialsAsync(player, other, [10]).ConfigureAwait(false);
        var before = player.Inventory!.Items.ToArray();

        await new ItemStackAction().StackItemsAsync(player, mix.Number, 30).ConfigureAwait(false);

        Assert.That(player.Inventory.Items, Is.EquivalentTo(before));
        Assert.That(player.Money, Is.EqualTo(10_000_000));
    }

    /// <summary>
    /// Verifies inability to pay does not remove valid materials.
    /// </summary>
    [Test]
    public async ValueTask InsufficientMoneyKeepsMaterialsAsync()
    {
        var (player, mix) = await CreatePlayerAsync(1).ConfigureAwait(false);
        await AddMaterialsAsync(player, mix, [20, 10]).ConfigureAwait(false);
        player.Money = 1_499_999;
        var before = player.Inventory!.Items.ToArray();

        await new ItemStackAction().StackItemsAsync(player, mix.Number, 30).ConfigureAwait(false);

        Assert.That(player.Inventory.Items, Is.EquivalentTo(before));
        Assert.That(player.Money, Is.EqualTo(1_499_999));
    }

    /// <summary>
    /// Verifies consuming materials provides room for the output even in a full inventory.
    /// </summary>
    [Test]
    public async ValueTask FullInventoryCanCombineAsync()
    {
        var (player, mix) = await CreatePlayerAsync(1).ConfigureAwait(false);
        await AddMaterialsAsync(player, mix, [20, 10]).ConfigureAwait(false);
        var blocker = CreateDefinition();
        while (player.Inventory!.FreeSlots.Any())
        {
            var item = player.PersistenceContext.CreateNew<Item>();
            item.Definition = blocker;
            Assert.That(await player.Inventory.AddItemAsync(item).ConfigureAwait(false), Is.True);
        }

        await new ItemStackAction().StackItemsAsync(player, mix.Number, 30).ConfigureAwait(false);

        Assert.That(CountJewels(player, mix), Is.EqualTo(30));
        Assert.That(player.Inventory.Items.Count(item => item.Definition == mix.MixedJewel), Is.EqualTo(1));
        Assert.That(player.Money, Is.EqualTo(8_500_000));
    }

    /// <summary>
    /// Verifies combining still requires talking to the correct NPC.
    /// </summary>
    [Test]
    public async ValueTask WrongNpcKeepsMaterialsAndMoneyAsync()
    {
        var (player, mix) = await CreatePlayerAsync(1).ConfigureAwait(false);
        await AddMaterialsAsync(player, mix, [20, 10]).ConfigureAwait(false);
        player.OpenedNpc = null;
        var before = player.Inventory!.Items.ToArray();

        await new ItemStackAction().StackItemsAsync(player, mix.Number, 30).ConfigureAwait(false);

        Assert.That(player.Inventory.Items, Is.EquivalentTo(before));
        Assert.That(player.Money, Is.EqualTo(10_000_000));
    }

    /// <summary>
    /// Verifies a repeated confirmation cannot consume the output bundle or charge again.
    /// </summary>
    [Test]
    public async ValueTask RepeatedRequestKeepsOutputAndChargesOnceAsync()
    {
        var (player, mix) = await CreatePlayerAsync(1).ConfigureAwait(false);
        await AddMaterialsAsync(player, mix, [10, 10, 10]).ConfigureAwait(false);
        var action = new ItemStackAction();
        await action.StackItemsAsync(player, mix.Number, 30).ConfigureAwait(false);
        var output = player.Inventory!.Items.Single();

        await action.StackItemsAsync(player, mix.Number, 30).ConfigureAwait(false);

        Assert.That(player.Inventory.Items.Single(), Is.SameAs(output));
        Assert.That(player.Money, Is.EqualTo(8_500_000));
    }

    /// <summary>
    /// Verifies the same material rule applies to inventory extensions.
    /// </summary>
    [Test]
    public async ValueTask ExtendedInventoryMaterialsCombineAsync()
    {
        var (player, mix) = await CreatePlayerAsync(1).ConfigureAwait(false);
        var character = player.SelectedCharacter!;
        await player.RemoveFromGameAsync().ConfigureAwait(false);
        character.InventoryExtensions = 1;
        await player.SetSelectedCharacterAsync(character).ConfigureAwait(false);
        player.OpenedNpc = new NonPlayerCharacter(null!, new MonsterDefinition { NpcWindow = NpcWindow.Lahap }, null!);
        foreach (var slot in new[] { InventoryConstants.FirstExtensionItemSlotIndex, (byte)(InventoryConstants.FirstExtensionItemSlotIndex + 1) })
        {
            var item = player.PersistenceContext.CreateNew<Item>();
            item.Definition = mix.MixedJewel;
            item.Level = slot == InventoryConstants.FirstExtensionItemSlotIndex ? (byte)1 : (byte)0;
            item.Durability = 1;
            Assert.That(await player.Inventory!.AddItemAsync(slot, item).ConfigureAwait(false), Is.True);
        }

        await new ItemStackAction().StackItemsAsync(player, mix.Number, 30).ConfigureAwait(false);

        Assert.That(player.Inventory!.Items.Count(), Is.EqualTo(1));
        Assert.That(player.Inventory.Items.Single().Level, Is.EqualTo(2));
        Assert.That(CountJewels(player, mix), Is.EqualTo(30));
        Assert.That(player.Money, Is.EqualTo(8_500_000));
    }

    /// <summary>
    /// Verifies larger bundles are consumed first and extra singles keep their identity.
    /// </summary>
    [Test]
    public async ValueTask ExtraSinglesAndOtherJewelsStayUntouchedAsync()
    {
        var (player, mix) = await CreatePlayerAsync(1).ConfigureAwait(false);
        await AddMaterialsAsync(player, mix, [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 20, 10]).ConfigureAwait(false);
        var singles = player.Inventory!.Items.Where(item => item.Definition == mix.SingleJewel).ToArray();
        var other = player.PersistenceContext.CreateNew<Item>();
        other.Definition = CreateDefinition();
        Assert.That(await player.Inventory.AddItemAsync(other).ConfigureAwait(false), Is.True);

        await new ItemStackAction().StackItemsAsync(player, mix.Number, 30).ConfigureAwait(false);

        Assert.That(player.Inventory.Items.Where(item => item.Definition == mix.SingleJewel), Is.EquivalentTo(singles));
        Assert.That(player.Inventory.Items, Does.Contain(other));
        Assert.That(CountJewels(player, mix), Is.EqualTo(40));
        Assert.That(player.Money, Is.EqualTo(8_500_000));
    }

    private static int CountJewels(Player player, JewelMix mix) =>
        player.Inventory!.Items.Sum(item => item.Definition == mix.SingleJewel ? 1 : item.Definition == mix.MixedJewel ? (item.Level + 1) * 10 : 0);

    private static async ValueTask<(Player Player, JewelMix Mix)> CreatePlayerAsync(byte number)
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var mix = new JewelMix { Number = number, SingleJewel = CreateDefinition(), MixedJewel = CreateDefinition() };
        Mock.Get(player.GameContext.Configuration).Setup(configuration => configuration.JewelMixes).Returns(new List<JewelMix> { mix });
        player.GameContext.Configuration.MaximumInventoryMoney = int.MaxValue;
        player.Money = 10_000_000;
        player.OpenedNpc = new NonPlayerCharacter(null!, new MonsterDefinition { NpcWindow = NpcWindow.Lahap }, null!);
        return (player, mix);
    }

    private static ItemDefinition CreateDefinition() => new() { Width = 1, Height = 1, Durability = 1 };

    private static async ValueTask AddMaterialsAsync(Player player, JewelMix mix, int[] materials)
    {
        foreach (var pieces in materials)
        {
            var item = player.PersistenceContext.CreateNew<Item>();
            item.Definition = pieces == 1 ? mix.SingleJewel : mix.MixedJewel;
            item.Level = pieces == 1 ? (byte)0 : (byte)((pieces / 10) - 1);
            item.Durability = 1;
            Assert.That(await player.Inventory!.AddItemAsync(item).ConfigureAwait(false), Is.True);
        }
    }
}
