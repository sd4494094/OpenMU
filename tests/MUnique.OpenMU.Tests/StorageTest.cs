// <copyright file="StorageTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Moq;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;

/// <summary>
/// Tests the <see cref="Storage"/>.
/// </summary>
[TestFixture]
public class StorageTest
{
    /// <summary>
    /// A failed return preserves occupied destination slots which share source slot numbers.
    /// </summary>
    /// <param name="freeSlots">The insufficient number of remaining bag slots.</param>
    /// <returns>The task.</returns>
    [TestCase(0)]
    [TestCase(1)]
    public async ValueTask FailedTakeAllPreservesEquippedSlotsAsync(int freeSlots)
    {
        var destination = new Storage(12 + 64, 12, 0, this.CreateItemStorage());
        var source = new Storage(32, this.CreateItemStorage());
        var equipped = new[] { this.GetItem(1, 3), this.GetItem(2, 2) };
        Assert.That(await destination.AddItemAsync(0, equipped[0]).ConfigureAwait(false), Is.True);
        Assert.That(await destination.AddItemAsync(1, equipped[1]).ConfigureAwait(false), Is.True);
        foreach (var slot in destination.FreeSlots.Take(64 - freeSlots).ToArray())
        {
            Assert.That(await destination.AddItemAsync(slot, this.GetItem(1, 1)).ConfigureAwait(false), Is.True);
        }

        var earlier = new[] { this.GetItem(1, 1), this.GetItem(1, 1) };
        Assert.That(await source.AddItemAsync(0, earlier[0]).ConfigureAwait(false), Is.True);
        Assert.That(await source.AddItemAsync(1, earlier[1]).ConfigureAwait(false), Is.True);
        var original = destination.Items.Select(item => (Item: item, Slot: item.ItemSlot)).ToArray();
        var originalFreeSlots = destination.FreeSlots.ToArray();

        Assert.That(await destination.TryTakeAllAsync(source).ConfigureAwait(false), Is.False);

        Assert.That(destination.Items, Is.EquivalentTo(original.Select(x => x.Item)));
        Assert.That(destination.ItemStorage.Items, Is.EquivalentTo(original.Select(x => x.Item)));
        Assert.That(original.All(x => x.Item.ItemSlot == x.Slot), Is.True);
        Assert.That(destination.GetItem(0), Is.SameAs(equipped[0]));
        Assert.That(destination.GetItem(1), Is.SameAs(equipped[1]));
        Assert.That(destination.FreeSlots, Is.EqualTo(originalFreeSlots));
        Assert.That(source.Items, Is.EquivalentTo(earlier));
        Assert.That(earlier.Select(item => item.ItemSlot), Is.EqualTo(new byte[] { 0, 1 }));
    }

    /// <summary>
    /// Tests the adding of a small item to the upper left corner of the storage.
    /// </summary>
    [Test]
    public async ValueTask AddItem1X1TopLeftAsync()
    {
        var itemStorage = this.CreateItemStorage();
        var storage = new Storage(12 + 64, 12, 0, itemStorage) as IStorage;
        var item = this.GetItem(1, 1);
        var added = await storage.AddItemAsync(12, item).ConfigureAwait(false);
        Assert.That(added, Is.True);
        Assert.That(storage.Items.Contains(item), Is.True);
        Assert.That(storage.FreeSlots.Contains((byte)12), Is.False);
    }

    /// <summary>
    /// Tests adding a 2x2 item to the right bottom corner of the storage.
    /// </summary>
    [Test]
    public async ValueTask AddItem2X2AtRightBottomAsync()
    {
        var itemStorage = this.CreateItemStorage();
        var storage = new Storage(12 + 64, 12, 0, itemStorage) as IStorage;
        var item = this.GetItem(2, 2);
        byte slot = 12 + (6 * 8) + 6;
        var added = await storage.AddItemAsync(slot, item).ConfigureAwait(false);
        Assert.That(added, Is.True);
        Assert.That(storage.Items.Contains(item), Is.True);
        Assert.That(storage.FreeSlots.Contains(slot), Is.False);
    }

    /// <summary>
    /// Tests if the adding of the item fails because the same slot is already in use by another item.
    /// </summary>
    [Test]
    public async ValueTask AddItemFailSpaceInUseAsync()
    {
        var itemStorage = this.CreateItemStorage();
        var storage = new Storage(12 + 64, 12, 0, itemStorage) as IStorage;
        await storage.AddItemAsync(12, this.GetItem(1, 1)).ConfigureAwait(false);
        var item = this.GetItem(1, 1);
        var added = await storage.AddItemAsync(12, item).ConfigureAwait(false);
        Assert.That(added, Is.False);
        Assert.That(storage.Items.Contains(item), Is.False);
        Assert.That(storage.FreeSlots.Contains((byte)12), Is.False);
    }

    /// <summary>
    /// Tests if the adding of the item one line below another 2x2 item fails.
    /// </summary>
    [Test]
    public async ValueTask AddItemFailSpaceInUseVerticalAsync()
    {
        var itemStorage = this.CreateItemStorage();
        var storage = new Storage(12 + 64, 12, 0, itemStorage) as IStorage;
        var addedItem = this.GetItem(2, 2);
        await storage.AddItemAsync(12, addedItem).ConfigureAwait(false);
        var added = await storage.AddItemAsync(13, this.GetItem(1, 1)).ConfigureAwait(false);
        Assert.That(added, Is.False);
        added = await storage.AddItemAsync(12 + 8, this.GetItem(1, 1)).ConfigureAwait(false);
        Assert.That(added, Is.False);
        Assert.That(storage.Items.Count(), Is.EqualTo(1));
        Assert.That(storage.Items.Contains(addedItem), Is.True);
        Assert.That(storage.FreeSlots.Contains((byte)12), Is.False);
        Assert.That(storage.FreeSlots.Contains((byte)13), Is.False);
        Assert.That(storage.FreeSlots.Contains((byte)(12 + 8)), Is.False);
        Assert.That(storage.FreeSlots.Contains((byte)(13 + 8)), Is.False);
    }

    /// <summary>
    /// Tests if adding an 2x1 item at the right border fails,
    /// because the left part of the item would hang over.
    /// </summary>
    [Test]
    public async ValueTask AddItem2X1FailRightBorderAsync()
    {
        var itemStorage = this.CreateItemStorage();
        var storage = new Storage(12 + 64, 12, 0, itemStorage) as IStorage;
        var added = await storage.AddItemAsync(12 + 7, this.GetItem(2, 1)).ConfigureAwait(false);
        Assert.That(added, Is.False);
        Assert.That(storage.FreeSlots.Contains((byte)(12 + 7)), Is.True);
    }

    /// <summary>
    /// Tests if adding an 1x2 item at the bottom border fails,
    /// because the bottom part of the item would hang over.
    /// </summary>
    [Test]
    public async ValueTask AddItem1X2FailBottomAsync()
    {
        var itemStorage = this.CreateItemStorage();
        var storage = new Storage(12 + 64, 12, 0, itemStorage) as IStorage;
        var added = await storage.AddItemAsync(12 + (7 * 8), this.GetItem(1, 2)).ConfigureAwait(false);
        Assert.That(added, Is.False);
        Assert.That(storage.FreeSlots.Contains((byte)(12 + (7 * 8))), Is.True);
    }

    private ItemDefinition GetItemDefintion(byte width, byte heigth)
    {
        var itemDefinition = new ItemDefinition
        {
            Durability = 100,
            Width = width,
            Height = heigth,
        };
        return itemDefinition;
    }

    private Item GetItem(byte width, byte heigth)
    {
        var item = new Item { Definition = this.GetItemDefintion(width, heigth) };
        item.Durability = item.Definition.Durability;
        return item;
    }

    private ItemStorage CreateItemStorage()
    {
        var storage = new Mock<ItemStorage>();
        storage.Setup(s => s.Items).Returns(new List<Item>());
        return storage.Object;
    }
}
