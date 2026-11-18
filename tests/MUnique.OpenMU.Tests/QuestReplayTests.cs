// <copyright file="QuestReplayTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Moq;
using MUnique.OpenMU.DataModel.Configuration.Quests;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions.Quests;
using MUnique.OpenMU.GameLogic.Views.Quest;
using MUnique.OpenMU.Persistence.BasicModel;
using QuestDefinition = MUnique.OpenMU.Persistence.BasicModel.QuestDefinition;
using QuestReward = MUnique.OpenMU.Persistence.BasicModel.QuestReward;

/// <summary>
/// Verifies that quest progress cannot be rewound to collect nonrepeatable rewards again.
/// </summary>
[TestFixture]
public class QuestReplayTests
{
    /// <summary>
    /// Completed quest numbers must remain completed even after another quest is finished or reloaded.
    /// </summary>
    /// <param name="group">The configured quest group.</param>
    /// <param name="lastNumber">The last finished quest number.</param>
    /// <param name="differentInstance">Whether the last finished quest uses a separate configuration instance.</param>
    [TestCase(0, 1, false)]
    [TestCase(0, 1, true)]
    [TestCase(0, 2, true)]
    [TestCase(42, 1, false)]
    [TestCase(42, 1, true)]
    [TestCase(42, 2, true)]
    public async Task CompletedQuestCannotBeRewardedAgainAsync(short group, short lastNumber, bool differentInstance)
    {
        var (player, quest, state) = await CreatePlayerAsync(group, lastNumber, differentInstance).ConfigureAwait(false);
        var lastFinished = state.LastFinishedQuest;

        await new QuestStartAction().StartQuestAsync(player, group, quest.StartingNumber).ConfigureAwait(false);
        await new QuestCompletionAction().CompleteQuestAsync(player, group, quest.Number).ConfigureAwait(false);

        Assert.That(state.ActiveQuest, Is.Null);
        Assert.That(state.LastFinishedQuest, Is.SameAs(lastFinished));
        Assert.That(player.Money, Is.EqualTo(1000));
        Assert.That(player.SelectedCharacter!.LevelUpPoints, Is.EqualTo(7));
        Mock.Get(player.ViewPlugIns.GetPlugIn<IQuestStartedPlugIn>()!).Verify(p => p.QuestStartedAsync(It.IsAny<DataModel.Configuration.Quests.QuestDefinition>()), Times.Never);
        Mock.Get(player.ViewPlugIns.GetPlugIn<IQuestCompletionResponsePlugIn>()!).Verify(p => p.QuestCompletedAsync(It.IsAny<DataModel.Configuration.Quests.QuestDefinition>()), Times.Never);
    }

    /// <summary>
    /// Selecting an already completed quest must not offer its acceptance dialogue.
    /// </summary>
    /// <param name="group">The configured quest group.</param>
    /// <param name="lastNumber">The last finished quest number.</param>
    /// <param name="differentInstance">Whether the last finished quest uses a separate configuration instance.</param>
    [TestCase(0, 1, false)]
    [TestCase(0, 1, true)]
    [TestCase(0, 2, true)]
    [TestCase(42, 1, false)]
    [TestCase(42, 1, true)]
    [TestCase(42, 2, true)]
    public async Task CompletedQuestCannotBeSelectedAsync(short group, short lastNumber, bool differentInstance)
    {
        var (player, quest, state) = await CreatePlayerAsync(group, lastNumber, differentInstance).ConfigureAwait(false);
        var lastFinished = state.LastFinishedQuest;

        await new QuestSelectAction().SelectQuestAsync(player, group, quest.StartingNumber).ConfigureAwait(false);

        Mock.Get(player.ViewPlugIns.GetPlugIn<IQuestStepInfoPlugIn>()!).Verify(p => p.ShowQuestStepInfoAsync(It.IsAny<short>(), It.IsAny<short>()), Times.Never);
        Assert.That(state.LastFinishedQuest, Is.SameAs(lastFinished));
        Assert.That(state.ActiveQuest, Is.Null);
        Assert.That(player.Money, Is.EqualTo(1000));
    }

    /// <summary>
    /// Explicitly repeatable quests can still grant their rewards, including after a later quest.
    /// </summary>
    /// <param name="lastNumber">The last finished quest number.</param>
    [TestCase(1)]
    [TestCase(2)]
    public async Task RepeatableQuestCanBeRewardedAgainAsync(short lastNumber)
    {
        var (player, quest, state) = await CreatePlayerAsync(42, lastNumber, true).ConfigureAwait(false);
        quest.Repeatable = true;

        await new QuestSelectAction().SelectQuestAsync(player, quest.Group, quest.StartingNumber).ConfigureAwait(false);
        Mock.Get(player.ViewPlugIns.GetPlugIn<IQuestStepInfoPlugIn>()!).Verify(p => p.ShowQuestStepInfoAsync(quest.Group, quest.StartingNumber), Times.Once);
        await new QuestStartAction().StartQuestAsync(player, quest.Group, quest.StartingNumber).ConfigureAwait(false);
        Assert.That(state.ActiveQuest, Is.SameAs(quest));
        await new QuestCompletionAction().CompleteQuestAsync(player, quest.Group, quest.Number).ConfigureAwait(false);

        Assert.That(state.ActiveQuest, Is.Null);
        Assert.That(state.LastFinishedQuest, Is.SameAs(quest));
        Assert.That(player.Money, Is.EqualTo(900));
        Assert.That(player.SelectedCharacter!.LevelUpPoints, Is.EqualTo(27));
        Mock.Get(player.ViewPlugIns.GetPlugIn<IQuestCompletionResponsePlugIn>()!).Verify(p => p.QuestCompletedAsync(quest), Times.Once);
    }

    /// <summary>
    /// A first quest or a later quest remains available, and duplicate completion does not grant rewards twice.
    /// </summary>
    /// <param name="hasPreviousQuest">Whether an earlier quest has been completed.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async Task NewQuestCanBeRewardedOnceAsync(bool hasPreviousQuest)
    {
        var (player, quest, state) = await CreatePlayerAsync(42, 0, true).ConfigureAwait(false);
        if (!hasPreviousQuest)
        {
            state.LastFinishedQuest = null!;
        }

        await new QuestSelectAction().SelectQuestAsync(player, quest.Group, quest.StartingNumber).ConfigureAwait(false);
        Mock.Get(player.ViewPlugIns.GetPlugIn<IQuestStepInfoPlugIn>()!).Verify(p => p.ShowQuestStepInfoAsync(quest.Group, quest.StartingNumber), Times.Once);
        await new QuestStartAction().StartQuestAsync(player, quest.Group, quest.StartingNumber).ConfigureAwait(false);
        Assert.That(state.ActiveQuest, Is.SameAs(quest));
        await new QuestCompletionAction().CompleteQuestAsync(player, quest.Group, quest.Number).ConfigureAwait(false);
        await new QuestCompletionAction().CompleteQuestAsync(player, quest.Group, quest.Number).ConfigureAwait(false);

        Assert.That(state.ActiveQuest, Is.Null);
        Assert.That(state.LastFinishedQuest, Is.SameAs(quest));
        Assert.That(player.Money, Is.EqualTo(900));
        Assert.That(player.SelectedCharacter!.LevelUpPoints, Is.EqualTo(27));
        Mock.Get(player.ViewPlugIns.GetPlugIn<IQuestCompletionResponsePlugIn>()!).Verify(p => p.QuestCompletedAsync(quest), Times.Once);
    }

    private static async ValueTask<(Player Player, QuestDefinition Quest, CharacterQuestState State)> CreatePlayerAsync(short group, short lastNumber, bool differentInstance)
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        player.Money = 1000;
        player.SelectedCharacter!.LevelUpPoints = 7;
        var quest = new QuestDefinition { Id = Guid.NewGuid(), Group = group, Number = 1, StartingNumber = 0, RequiredStartMoney = 100 };
        quest.Rewards.Add(new QuestReward { RewardType = QuestRewardType.LevelUpPoints, Value = 20 });
        var state = new CharacterQuestState
        {
            Group = group,
            LastFinishedQuest = differentInstance ? new QuestDefinition { Id = Guid.NewGuid(), Group = group, Number = lastNumber } : quest,
        };
        Mock.Get(player.SelectedCharacter).Setup(c => c.QuestStates).Returns(new List<MUnique.OpenMU.DataModel.Entities.CharacterQuestState> { state });
        var npc = new MonsterDefinition();
        npc.Quests.Add(quest);
        player.OpenedNpc = new NonPlayerCharacter(null!, npc, player.CurrentMap!);
        return (player, quest, state);
    }
}
