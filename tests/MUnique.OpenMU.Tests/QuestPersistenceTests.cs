// <copyright file="QuestPersistenceTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Globalization;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Quests;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions.Quests;
using MUnique.OpenMU.GameLogic.Views.Quest;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.PlugIns;
using B = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Checks quest acknowledgements against the payment, material and reward commit.
/// </summary>
[TestFixture]
public class QuestPersistenceTests
{
    /// <summary>
    /// Accepting a charged quest cannot publish success before the active quest and payment commit.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task StartWaitsForCommitAsync()
    {
        var (player, context, quest, state, _) = await CreateAsync(false).ConfigureAwait(false);
        await using var ownedPlayer = player;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(async () =>
        {
            Assert.That(player.Money, Is.EqualTo(900));
            Assert.That(state.ActiveQuest, Is.SameAs(quest));
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return true;
        });
        var view = Mock.Get(player.ViewPlugIns.GetPlugIn<IQuestStartedPlugIn>()!);
        var start = new QuestStartAction().StartQuestAsync(player, quest.Group, quest.Number).AsTask();
        try
        {
            await Task.WhenAny(start, entered.Task).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.That(entered.Task.IsCompleted, Is.True, "Quest acceptance bypassed persistence.");
            view.Verify(p => p.QuestStartedAsync(quest), Times.Never);
        }
        finally
        {
            release.TrySetResult();
            await start.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        view.Verify(p => p.QuestStartedAsync(quest), Times.Once);
    }

    /// <summary>
    /// Every supported reward enters the same commit before the completion response.
    /// </summary>
    /// <param name="kind">The reward kind.</param>
    /// <returns>The task.</returns>
    [TestCase(QuestRewardType.LevelUpPoints)]
    [TestCase(QuestRewardType.Money)]
    [TestCase(QuestRewardType.Item)]
    [TestCase(QuestRewardType.Attribute)]
    [TestCase(QuestRewardType.Skill)]
    [TestCase(QuestRewardType.Experience)]
    [TestCase(QuestRewardType.CharacterEvolutionFirstToSecond)]
    [TestCase(QuestRewardType.CharacterEvolutionSecondToThird)]
    public async Task CompletionWaitsForCommitAsync(QuestRewardType kind)
    {
        var (player, context, quest, state, ingredient) = await CreateAsync(true, kind).ConfigureAwait(false);
        await using var ownedPlayer = player;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(async () =>
        {
            Assert.That(state.ActiveQuest, Is.Null);
            Assert.That(state.LastFinishedQuest, Is.SameAs(quest));
            Assert.That(player.Inventory!.Items, Does.Not.Contain(ingredient));
            switch (kind)
            {
                case QuestRewardType.LevelUpPoints:
                    Assert.That(player.SelectedCharacter!.LevelUpPoints, Is.EqualTo(17));
                    break;
                case QuestRewardType.Money:
                    Assert.That(player.Money, Is.EqualTo(1010));
                    break;
                case QuestRewardType.Item:
                    Assert.That(player.Inventory!.Items.Single().Definition, Is.SameAs(quest.Rewards.Single().ItemReward!.Definition));
                    break;
                case QuestRewardType.Attribute:
                    Assert.That(player.SelectedCharacter!.Attributes.Single(a => a.Definition == quest.Rewards.Single().AttributeReward).Value, Is.EqualTo(10));
                    break;
                case QuestRewardType.Skill:
                    Assert.That(player.SelectedCharacter!.LearnedSkills.Single().Skill, Is.SameAs(quest.Rewards.Single().SkillReward));
                    break;
                case QuestRewardType.Experience:
                    Assert.That(player.SelectedCharacter!.Experience, Is.EqualTo(10));
                    break;
                default:
                    Assert.That(player.SelectedCharacter!.CharacterClass!.Number, Is.EqualTo(1));
                    break;
            }

            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return true;
        });
        var view = Mock.Get(player.ViewPlugIns.GetPlugIn<IQuestCompletionResponsePlugIn>()!);
        var completion = new QuestCompletionAction().CompleteQuestAsync(player, quest.Group, quest.Number).AsTask();
        try
        {
            await Task.WhenAny(completion, entered.Task).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.That(entered.Task.IsCompleted, Is.True, "Quest completion bypassed persistence.");
            view.Verify(p => p.QuestCompletedAsync(quest), Times.Never);
        }
        finally
        {
            release.TrySetResult();
            await completion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        view.Verify(p => p.QuestCompletedAsync(quest), Times.Once);
    }

    /// <summary>
    /// A failed or ambiguous save closes the session without success or another application of the outcome.
    /// </summary>
    /// <param name="completion">Whether to complete rather than accept.</param>
    /// <param name="throws">Whether persistence throws rather than returning false.</param>
    /// <returns>The task.</returns>
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task UnconfirmedSaveDisconnectsAndRejectsReplayAsync(bool completion, bool throws)
    {
        var (player, context, quest, _, _) = await CreateAsync(completion).ConfigureAwait(false);
        await using var ownedPlayer = player;
        var character = player.SelectedCharacter!;
        var calls = 0;
        context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(() =>
        {
            if (++calls > 1)
            {
                return new ValueTask<bool>(true); // Teardown retries the same outcome, without refunding or applying it again.
            }

            if (throws)
            {
                throw new InvalidOperationException("Quest commit acknowledgement was lost.");
            }

            return new ValueTask<bool>(false);
        });
        var action = completion
            ? new Func<ValueTask>(() => new QuestCompletionAction().CompleteQuestAsync(player, quest.Group, quest.Number))
            : () => new QuestStartAction().StartQuestAsync(player, quest.Group, quest.Number);
        await action().ConfigureAwait(false);
        await action().ConfigureAwait(false);

        Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.Finished));
        Assert.That(character.Inventory!.Money, Is.EqualTo(completion ? 1000 : 900));
        Assert.That(character.LevelUpPoints, Is.EqualTo(completion ? 17 : 7));
        Mock.Get(player.ViewPlugIns.GetPlugIn<IQuestStartedPlugIn>()!).Verify(p => p.QuestStartedAsync(quest), Times.Never);
        Mock.Get(player.ViewPlugIns.GetPlugIn<IQuestCompletionResponsePlugIn>()!).Verify(p => p.QuestCompletedAsync(quest), Times.Never);
    }

    /// <summary>
    /// A full inventory cannot expose its item reward to another player before the quest commit.
    /// </summary>
    /// <param name="reject">Whether persistence rejects the outcome.</param>
    /// <param name="throws">Whether the rejection throws.</param>
    /// <returns>The task.</returns>
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task FullInventoryRewardWaitsForCommitAsync(bool reject, bool throws)
    {
        var (player, context, quest, _, ingredient) = await CreateAsync(true, QuestRewardType.Item).ConfigureAwait(false);
        await using var ownedPlayer = player;
        var map = player.CurrentMap!;
        quest.RequiredItems.Clear(); // Keep the material slot occupied too.
        var filler = new B.ItemDefinition { Width = 1, Height = 1, Durability = 1 };
        for (byte slot = 12; slot < 76; slot++)
        {
            if (slot != ingredient.ItemSlot)
            {
                Assert.That(await player.Inventory!.AddItemAsync(slot, new B.Item { Definition = filler, Durability = 1 }).ConfigureAwait(false), Is.True);
            }
        }

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        GroundItemDelivery? stagedDelivery = null;
        context.Setup(c => c.CreateNew<GroundItemDelivery>(It.IsAny<object?[]>())).Returns(() => stagedDelivery = new B.GroundItemDelivery { Id = Guid.NewGuid() });
        context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(async () =>
        {
            Assert.That(stagedDelivery, Is.Not.Null, "The ground marker must be in the task commit before publication.");
            Assert.That(stagedDelivery!.HostServerId, Is.EqualTo(map.HostServer!.GetId()));
            Assert.That(stagedDelivery.MapDefinitionId, Is.EqualTo(map.Definition.GetId()));
            Assert.That(stagedDelivery.MapInstanceId, Is.EqualTo(map.InstanceId));
            Assert.That(stagedDelivery.OwnerCharacterId, Is.EqualTo(player.SelectedCharacter!.GetId()));
            Assert.That(stagedDelivery.ExpiresAt, Is.EqualTo(stagedDelivery.DroppedAt.Add(map.ItemDropDuration)));
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            if (throws)
            {
                throw new InvalidOperationException("Full inventory quest reward was not confirmed.");
            }

            return !reject;
        });
        var completion = new QuestCompletionAction().CompleteQuestAsync(player, quest.Group, quest.Number).AsTask();
        try
        {
            await Task.WhenAny(completion, entered.Task).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.That(entered.Task.IsCompleted, Is.True, "Full inventory reward bypassed persistence.");
            Assert.That(map.GetDropsInRange(player.Position, 1), Is.Empty);
        }
        finally
        {
            release.TrySetResult();
            await completion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        if (reject)
        {
            Assert.That(map.GetDropsInRange(player.Position, 1), Is.Empty);
            Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.Finished));
            context.Verify(c => c.Detach(It.IsAny<Item>()), Times.Never);
            Mock.Get(player.ViewPlugIns.GetPlugIn<IQuestCompletionResponsePlugIn>()!).Verify(p => p.QuestCompletedAsync(quest), Times.Never);
            return;
        }

        var drop = map.GetDropsInRange(player.Position, 1).OfType<DroppedItem>().Single();
        Assert.That(drop.Item.GroundDelivery, Is.SameAs(stagedDelivery));
        context.Verify(c => c.Detach(drop.Item), Times.Once);
        Assert.That(drop.Item.Definition, Is.SameAs(quest.Rewards.Single().ItemReward!.Definition));
    }

    /// <summary>
    /// Completion replay during a blocked save is serialized and never grants a second reward.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task ConcurrentCompletionAppliesRewardOnceAsync()
    {
        var (player, context, quest, _, _) = await CreateAsync(true).ConfigureAwait(false);
        await using var ownedPlayer = player;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(async () =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return true;
        });
        var action = new QuestCompletionAction();
        var first = action.CompleteQuestAsync(player, quest.Group, quest.Number).AsTask();
        Task? second = null;
        try
        {
            await Task.WhenAny(first, entered.Task).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.That(entered.Task.IsCompleted, Is.True, "Quest completion bypassed persistence.");
            second = action.CompleteQuestAsync(player, quest.Group, quest.Number).AsTask();
            Assert.That(second.IsCompleted, Is.False, "Replay escaped the persistence scope.");
        }
        finally
        {
            release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (second is not null)
            {
                await second.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }

        Assert.That(player.SelectedCharacter!.LevelUpPoints, Is.EqualTo(17));
        context.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A second save failure during disconnect still cannot leave a session able to replay rewards.
    /// </summary>
    /// <param name="completion">Whether to complete rather than accept.</param>
    /// <param name="throws">Whether both save attempts throw.</param>
    /// <returns>The task.</returns>
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task TeardownSaveFailureCannotAcknowledgeOrReplayAsync(bool completion, bool throws)
    {
        var (player, context, quest, _, _) = await CreateAsync(completion).ConfigureAwait(false);
        await using var ownedPlayer = player;
        var character = player.SelectedCharacter!;
        context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(() =>
            throws ? throw new InvalidOperationException("Quest persistence unavailable.") : new ValueTask<bool>(false));

        if (completion)
        {
            await new QuestCompletionAction().CompleteQuestAsync(player, quest.Group, quest.Number).ConfigureAwait(false);
        }
        else
        {
            await new QuestStartAction().StartQuestAsync(player, quest.Group, quest.Number).ConfigureAwait(false);
        }

        await new QuestStartAction().StartQuestAsync(player, quest.Group, quest.Number).ConfigureAwait(false);
        await new QuestCompletionAction().CompleteQuestAsync(player, quest.Group, quest.Number).ConfigureAwait(false);
        Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.Finished));
        Assert.That(character.Inventory!.Money, Is.EqualTo(completion ? 1000 : 900));
        Assert.That(character.LevelUpPoints, Is.EqualTo(completion ? 17 : 7));
        Mock.Get(player.ViewPlugIns.GetPlugIn<IQuestStartedPlugIn>()!).Verify(p => p.QuestStartedAsync(quest), Times.Never);
        Mock.Get(player.ViewPlugIns.GetPlugIn<IQuestCompletionResponsePlugIn>()!).Verify(p => p.QuestCompletedAsync(quest), Times.Never);
    }

    /// <summary>
    /// A transport failure after a confirmed completion cannot restore materials or apply rewards again.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task FailedResponseDoesNotRepeatCommittedRewardAsync()
    {
        var (player, context, quest, state, ingredient) = await CreateAsync(true).ConfigureAwait(false);
        await using var ownedPlayer = player;
        var committed = false;
        context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(() =>
        {
            committed = true;
            return new ValueTask<bool>(true);
        });
        Mock.Get(player.ViewPlugIns.GetPlugIn<IQuestCompletionResponsePlugIn>()!)
            .Setup(p => p.QuestCompletedAsync(quest))
            .Callback(() => Assert.That(committed, Is.True))
            .Throws(new InvalidOperationException("Response transport failed."));

        var action = new QuestCompletionAction();
        await action.CompleteQuestAsync(player, quest.Group, quest.Number).ConfigureAwait(false);
        await action.CompleteQuestAsync(player, quest.Group, quest.Number).ConfigureAwait(false);
        Assert.That(player.SelectedCharacter!.LevelUpPoints, Is.EqualTo(17));
        Assert.That(player.Inventory!.Items, Does.Not.Contain(ingredient));
        Assert.That(state.LastFinishedQuest, Is.SameAs(quest));
        Assert.That(state.ActiveQuest, Is.Null);
        context.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Legacy compensation notifications retain a non-default minimum-level quest context.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task AttributeNotificationRetainsActiveQuestContextAsync()
    {
        var (player, _, quest, state, _) = await CreateAsync(true, QuestRewardType.Attribute).ConfigureAwait(false);
        await using var ownedPlayer = player;
        player.Attributes![Stats.Level] = 250;
        quest.MinimumCharacterLevel = 230;
        var reward = quest.Rewards.Single();
        reward.AttributeReward = Stats.PointsPerLevelUp;
        reward.Value = 1;
        var notified = false;
        Mock.Get(player.ViewPlugIns.GetPlugIn<ILegacyQuestRewardPlugIn>()!)
            .Setup(p => p.ShowAsync(player, QuestRewardType.Attribute, 1, Stats.PointsPerLevelUp))
            .Callback(() =>
            {
                Assert.That(state.ActiveQuest, Is.SameAs(quest), "The legacy view still reads this context to calculate compensation.");
                Assert.That(state.ActiveQuest!.MinimumCharacterLevel, Is.EqualTo(230));
                notified = true;
            });

        await new QuestCompletionAction().CompleteQuestAsync(player, quest.Group, quest.Number).ConfigureAwait(false);
        Assert.That(notified, Is.True);
        Assert.That(player.SelectedCharacter!.LevelUpPoints, Is.EqualTo(27));
        Assert.That(state.ActiveQuest, Is.Null);
    }

    private static async ValueTask<(Player Player, Mock<IPlayerContext> Context, B.QuestDefinition Quest, B.CharacterQuestState State, Item Ingredient)> CreateAsync(bool active, QuestRewardType kind = QuestRewardType.LevelUpPoints)
    {
        var config = PlayerTestHelper.CreateGameConfiguration();
        config.Maps.Clear();
        config.Maps.Add(new B.GameMapDefinition { Id = Guid.NewGuid(), TerrainData = new byte[ushort.MaxValue + 3] });
        config.MaximumLevel = 400;
        config.ExperienceFormula = "level * 1000";
        config.MaximumInventoryMoney = int.MaxValue;
        config.ItemDropDuration = TimeSpan.FromMinutes(5);
        var context = new Mock<IPlayerContext>();
        context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(true));
        context.Setup(c => c.CreateNew<Item>(It.IsAny<object?[]>())).Returns(() => new B.Item { Id = Guid.NewGuid() });
        context.Setup(c => c.CreateNew<GroundItemDelivery>(It.IsAny<object?[]>())).Returns(() => new B.GroundItemDelivery { Id = Guid.NewGuid() });
        context.Setup(c => c.DeleteAsync(It.IsAny<GroundItemDelivery>())).Returns(new ValueTask<bool>(true));
        context.Setup(c => c.CreateNew<SkillEntry>(It.IsAny<object?[]>())).Returns(() => new B.SkillEntry { Id = Guid.NewGuid() });
        context.Setup(c => c.CreateNew<AttributeSystem.StatAttribute>(It.IsAny<object?[]>()))
            .Returns((object?[] args) => new B.StatAttribute((AttributeSystem.AttributeDefinition)args[0]!, Convert.ToSingle(args[1], CultureInfo.InvariantCulture)) { Id = Guid.NewGuid() });
        var provider = new Mock<IPersistenceContextProvider>();
        provider.Setup(p => p.CreateNewPlayerContext(It.IsAny<GameConfiguration>())).Returns(context.Object);
        var maps = new MapInitializer(config, NullLogger<MapInitializer>.Instance, NullDropGenerator.Instance, null);
        var game = new GameContext(config, provider.Object, maps, NullLoggerFactory.Instance,
            new PlugInManager(null, NullLoggerFactory.Instance, null, null), NullDropGenerator.Instance, new ConfigurationChangeMediator());
        maps.PlugInManager = game.PlugInManager;
        maps.PathFinderPool = game.PathFinderPool;
        var player = await PlayerTestHelper.CreatePlayerAsync(game, Guid.NewGuid()).ConfigureAwait(false);
        player.CurrentMap!.BindHost(new B.GameServerDefinition { Id = Guid.NewGuid() });
        player.Inventory!.ItemStorage.Money = 1000;
        player.SelectedCharacter!.LevelUpPoints = 7;
        var quest = new B.QuestDefinition { Id = Guid.NewGuid(), Group = 42, Number = 1, StartingNumber = 1, RequiredStartMoney = 100 };
        var definition = new B.ItemDefinition { Id = Guid.NewGuid(), Width = 1, Height = 1, Durability = 1 };
        var ingredient = new B.Item { Id = Guid.NewGuid(), Definition = definition, Durability = 1 };
        await player.Inventory.AddItemAsync(12, ingredient).ConfigureAwait(false);
        quest.RequiredItems.Add(new B.QuestItemRequirement { Item = definition, MinimumNumber = 1 });
        var reward = new B.QuestReward { RewardType = kind, Value = 10 };
        reward.ItemReward = new B.Item { Id = Guid.NewGuid(), Definition = definition, Durability = 1 };
        reward.SkillReward = new B.Skill { Id = Guid.NewGuid(), Number = 1 };
        reward.AttributeReward = new B.AttributeDefinition { Id = Guid.NewGuid() };
        quest.Rewards.Add(reward);
        var state = new B.CharacterQuestState { Group = quest.Group, ActiveQuest = active ? quest : null! };
        Mock.Get(player.SelectedCharacter).Setup(c => c.QuestStates).Returns(new List<CharacterQuestState> { state });
        Mock.Get(player.SelectedCharacter.CharacterClass!).Setup(c => c.NextGenerationClass).Returns(new B.CharacterClass { Number = 1, IsActive = true });
        var npc = new B.MonsterDefinition();
        npc.Quests.Add(quest);
        player.OpenedNpc = new NonPlayerCharacter(null!, npc, player.CurrentMap!);
        return (player, context, quest, state, ingredient);
    }
}
