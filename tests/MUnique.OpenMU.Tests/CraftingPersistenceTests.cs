// <copyright file="CraftingPersistenceTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
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
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.PlugIns;
using B = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Verifies the durable boundary of both successful and failed crafting rolls.
/// </summary>
[TestFixture]
public class CraftingPersistenceTests
{
    /// <summary>
    /// A result is not published until payment, consumed inputs and current outputs have an owner at commit.
    /// </summary>
    /// <param name="success">Whether the crafting roll succeeds.</param>
    /// <returns>The task.</returns>
    [TestCase(true)]
    [TestCase(false)]
    public async Task ResultWaitsForCommitAsync(bool success)
    {
        var (player, context, ingredient) = await CreateAsync(success).ConfigureAwait(false);
        await using var ownedPlayer = player;
        var character = player.SelectedCharacter!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(async () =>
        {
            Assert.That(player.Money, Is.EqualTo(990));
            Assert.That(player.TemporaryStorage!.Items, Does.Not.Contain(ingredient));
            Assert.That(character.RecoveryStorage?.Items ?? [], Is.EquivalentTo(player.TemporaryStorage.Items));
            if (success)
            {
                Assert.That(character.RecoveryStorage!.Items, Has.Count.EqualTo(1));
            }

            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return true;
        });
        var view = Mock.Get(player.ViewPlugIns.GetPlugIn<IShowItemCraftingResultPlugIn>()!);
        var mix = new ItemCraftAction().MixItemsAsync(player, 1, 0).AsTask();
        try
        {
            await Task.WhenAny(mix, entered.Task).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.That(entered.Task.IsCompleted, Is.True, "The result bypassed persistence.");
            view.Verify(p => p.ShowResultAsync(It.IsAny<CraftingResult>(), It.IsAny<Item?>()), Times.Never);
        }
        finally
        {
            release.TrySetResult();
            await mix.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        view.Verify(p => p.ShowResultAsync(success ? CraftingResult.Success : CraftingResult.Failed, It.IsAny<Item?>()), Times.Once);
        Assert.That(character.RecoveryStorage?.Items ?? [], Is.Empty, "Live temporary items must not also be recoverable.");
        Assert.That(player.TemporaryStorage!.Items.Count(), Is.EqualTo(success ? 1 : 0));
        Assert.That(player.BackupInventory, Is.Null);
    }

    /// <summary>
    /// A rejected or uncertain save cannot leave a playable session or acknowledge the unconfirmed roll.
    /// </summary>
    /// <param name="throws">Whether saving throws instead of returning false.</param>
    /// <returns>The task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task UnconfirmedSaveDisconnectsBeforeAnotherMixAsync(bool throws)
    {
        var (player, context, _) = await CreateAsync(true).ConfigureAwait(false);
        await using var ownedPlayer = player;
        var character = player.SelectedCharacter!;
        var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(async () =>
        {
            if (Interlocked.Increment(ref calls) > 1)
            {
                return true; // Ordinary disconnect reconciles the same outcome, without rolling again.
            }

            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            if (throws)
            {
                throw new InvalidOperationException("Unconfirmed crafting commit.");
            }

            return false;
        });
        var action = new ItemCraftAction();
        var first = action.MixItemsAsync(player, 1, 0).AsTask();
        try
        {
            await Task.WhenAny(first, entered.Task).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.That(entered.Task.IsCompleted, Is.True, "The mix never attempted to save.");
        }
        finally
        {
            release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        await action.MixItemsAsync(player, 1, 0).ConfigureAwait(false);
        Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.Finished));
        Mock.Get(player.ViewPlugIns.GetPlugIn<IShowItemCraftingResultPlugIn>()!)
            .Verify(p => p.ShowResultAsync(It.IsAny<CraftingResult>(), It.IsAny<Item?>()), Times.Never);
        Assert.That(character.Inventory!.Money, Is.EqualTo(990));
        Assert.That(character.Inventory.Items, Has.Count.EqualTo(1));
        Assert.That(character.RecoveryStorage?.Items ?? [], Is.Empty);
    }

    /// <summary>
    /// Failure to deliver a committed result does not restore consumed ingredients or charge a second time.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task ResultViewFailureKeepsCommittedOutcomeAsync()
    {
        var (player, context, ingredient) = await CreateAsync(true).ConfigureAwait(false);
        await using var ownedPlayer = player;
        var committed = false;
        context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(() =>
        {
            committed = true;
            return new ValueTask<bool>(true);
        });
        Mock.Get(player.ViewPlugIns.GetPlugIn<IShowItemCraftingResultPlugIn>()!)
            .Setup(p => p.ShowResultAsync(It.IsAny<CraftingResult>(), It.IsAny<Item?>()))
            .Callback(() => Assert.That(committed, Is.True))
            .Throws(new InvalidOperationException("Result transport failed."));
        await new ItemCraftAction().MixItemsAsync(player, 1, 0).ConfigureAwait(false);
        Assert.That(committed, Is.True);
        Assert.That(player.Money, Is.EqualTo(990));
        Assert.That(player.TemporaryStorage!.Items.Single(), Is.Not.SameAs(ingredient));
        Assert.That(player.BackupInventory, Is.Null);
    }

    /// <summary>
    /// All current outputs, including a modified input, retain an owner without leaving live recovery duplicates.
    /// </summary>
    /// <param name="modifiedInput">Whether the recipe upgrades its input instead of creating several outputs.</param>
    /// <returns>The task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task RepeatedSavesStageOnlyCurrentItemsAsync(bool modifiedInput)
    {
        var (player, context, ingredient) = await CreateAsync(true).ConfigureAwait(false);
        await using var ownedPlayer = player;
        var character = player.SelectedCharacter!;
        var settings = player.OpenedNpc!.Definition.ItemCraftings.Single().SimpleCraftingSettings!;
        if (modifiedInput)
        {
            settings.RequiredItems.Single().SuccessResult = MixResult.StaysAsIs;
            settings.RequiredItems.Single().Reference = 1;
            settings.ResultItems.Clear();
            settings.ResultItems.Add(new B.ItemCraftingResultItem { Reference = 1, AddLevel = 1 });
        }
        else
        {
            settings.ResultItems.Add(new B.ItemCraftingResultItem { ItemDefinition = ingredient.Definition, Durability = 1 });
        }

        var retained = new B.Item { Id = Guid.NewGuid(), Definition = ingredient.Definition, Durability = 1 };
        character.RecoveryStorage = new B.ItemStorage { Id = Guid.NewGuid() };
        character.RecoveryStorage.Items.Add(retained);
        var saves = 0;
        context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(() =>
        {
            saves++;
            Assert.That(character.RecoveryStorage.Items, Is.EquivalentTo(player.TemporaryStorage!.Items.Append(retained)));
            return new ValueTask<bool>(true);
        });
        await new ItemCraftAction().MixItemsAsync(player, 1, 0).ConfigureAwait(false);
        Assert.That(player.TemporaryStorage!.Items.Count(), Is.EqualTo(modifiedInput ? 1 : 2));
        if (modifiedInput)
        {
            Assert.That(player.TemporaryStorage.Items.Single(), Is.SameAs(ingredient));
            Assert.That(ingredient.Level, Is.EqualTo(1));
        }

        Assert.That(await player.SaveProgressAsync().ConfigureAwait(false), Is.True);
        Assert.That(character.RecoveryStorage.Items, Is.EquivalentTo(new[] { retained }));
        Assert.That(saves, Is.EqualTo(2));
        Assert.That(await player.Inventory!.TryTakeAllAsync(player.TemporaryStorage).ConfigureAwait(false), Is.True);
        Assert.That(await player.SaveProgressAsync().ConfigureAwait(false), Is.True);
        Assert.That(saves, Is.EqualTo(3));
        Assert.That(character.RecoveryStorage.Items, Is.EquivalentTo(new[] { retained }));
        Assert.That(character.Inventory!.Items.Count, Is.EqualTo(modifiedInput ? 1 : 2));
    }

    private static async ValueTask<(Player Player, Mock<IPlayerContext> Context, Item Ingredient)> CreateAsync(bool success)
    {
        var config = PlayerTestHelper.CreateGameConfiguration();
        var context = new Mock<IPlayerContext>();
        context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(true));
        context.Setup(c => c.CreateNew<Item>(It.IsAny<object?[]>())).Returns(() => new B.Item { Id = Guid.NewGuid() });
        context.Setup(c => c.CreateNew<ItemStorage>(It.IsAny<object?[]>())).Returns(() => new B.ItemStorage { Id = Guid.NewGuid() });
        var provider = new Mock<IPersistenceContextProvider>();
        provider.Setup(p => p.CreateNewPlayerContext(It.IsAny<GameConfiguration>())).Returns(context.Object);
        var maps = new MapInitializer(config, NullLogger<MapInitializer>.Instance, NullDropGenerator.Instance, null);
        var game = new GameContext(config, provider.Object, maps, NullLoggerFactory.Instance,
            new PlugInManager(null, NullLoggerFactory.Instance, null, null), NullDropGenerator.Instance, new ConfigurationChangeMediator());
        maps.PlugInManager = game.PlugInManager;
        maps.PathFinderPool = game.PathFinderPool;
        var player = await PlayerTestHelper.CreatePlayerAsync(game).ConfigureAwait(false);
        var definition = new B.ItemDefinition { Width = 1, Height = 1, Durability = 1 };
        var ingredient = new B.Item { Id = Guid.NewGuid(), Definition = definition, Durability = 1 };
        await player.Inventory!.AddItemAsync(12, ingredient).ConfigureAwait(false);
        player.Inventory.ItemStorage.Money = 1000;
        var settings = new B.SimpleCraftingSettings { SuccessPercent = success ? (byte)100 : (byte)0, Money = 10, ResultItemSelect = ResultItemSelection.All };
        var requirement = new B.ItemCraftingRequiredItem
        {
            MinimumAmount = 1, MaximumAmount = 1, MaximumItemLevel = 15,
            SuccessResult = MixResult.Disappear, FailResult = MixResult.Disappear,
        };
        requirement.PossibleItems.Add(definition);
        settings.RequiredItems.Add(requirement);
        settings.ResultItems.Add(new B.ItemCraftingResultItem { ItemDefinition = definition, Durability = 1 });
        var npc = new B.MonsterDefinition { NpcWindow = NpcWindow.ChaosMachine };
        npc.ItemCraftings.Add(new B.ItemCrafting { Number = 1, SimpleCraftingSettings = settings });
        await new TalkNpcAction().TalkToNpcAsync(player, new NonPlayerCharacter(null!, npc, null!)).ConfigureAwait(false);
        await player.Inventory.RemoveItemAsync(ingredient).ConfigureAwait(false);
        await player.TemporaryStorage!.AddItemAsync(0, ingredient).ConfigureAwait(false);
        return (player, context, ingredient);
    }
}
