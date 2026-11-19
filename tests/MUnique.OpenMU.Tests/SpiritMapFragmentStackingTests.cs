// <copyright file="SpiritMapFragmentStackingTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.GameLogic.PlugIns;

/// <summary>
/// Tests Spirit Map conversion through the inventory movement action.
/// </summary>
[TestFixture]
public class SpiritMapFragmentStackingTests
{
    /// <summary>
    /// A full stack becomes one map and an incomplete stack keeps its fragments.
    /// </summary>
    /// <param name="targetCount">The initial fragment count in the destination slot.</param>
    /// <param name="sourceCount">The initial fragment count in the source slot.</param>
    /// <param name="expectedNumber">The item number in the destination after stacking.</param>
    /// <param name="expectedCount">The quantity in the destination after stacking.</param>
    /// <param name="expectedRemainder">The fragments left at the source.</param>
    [TestCase(3, 1, 145, 4, 0)]
    [TestCase(4, 1, 146, 1, 0)]
    [TestCase(4, 2, 146, 1, 1)]
    public async Task StackFragmentsAsync(byte targetCount, byte sourceCount, short expectedNumber, byte expectedCount, byte expectedRemainder)
    {
        await using var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var config = player.GameContext.Configuration;
        var fragments = new ItemDefinition { Group = 13, Number = 145, Width = 1, Height = 1, Durability = 5 };
        var map = new ItemDefinition { Group = 13, Number = 146, Width = 1, Height = 1, Durability = 1 };
        config.Items.Add(fragments);
        config.Items.Add(map);
        player.GameContext.PlugInManager.RegisterPlugInAtPlugInPoint<IItemStackedPlugIn>(new SpiritMapFragmentStackedPlugIn());

        var inventory = player.Inventory!;
        var source = new Item { Definition = fragments, Durability = sourceCount };
        var target = new Item { Definition = fragments, Durability = targetCount };
        Assert.That(await inventory.AddItemAsync(20, source).ConfigureAwait(false), Is.True);
        Assert.That(await inventory.AddItemAsync(21, target).ConfigureAwait(false), Is.True);

        await new MoveItemAction().MoveItemAsync(player, 20, Storages.Inventory, 21, Storages.Inventory).ConfigureAwait(false);

        Assert.That(inventory.GetItem(21), Is.SameAs(target));
        Assert.That(target.Definition!.Number, Is.EqualTo(expectedNumber));
        Assert.That(target.Durability, Is.EqualTo(expectedCount));
        Assert.That(inventory.GetItem(20)?.Durability ?? 0, Is.EqualTo(expectedRemainder));
    }
}
