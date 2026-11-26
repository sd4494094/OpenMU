// <copyright file="QuestItemDropTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Configuration.Quests;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using BasicModel = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Verifies quest-only item drops independently of the generic monster item pool.
/// </summary>
[TestFixture]
public class QuestItemDropTest
{
    /// <summary>
    /// Tests the level boundaries and active quest requirement of the ring of honor.
    /// </summary>
    /// <param name="monsterLevel">The killed monster level.</param>
    /// <param name="activeQuest">Whether the character has accepted the quest.</param>
    /// <param name="expectedDrop">Whether the quest item should be generated.</param>
    [TestCase(71, true, false)]
    [TestCase(72, true, true)]
    [TestCase(92, true, true)]
    [TestCase(96, true, true)]
    [TestCase(108, true, true)]
    [TestCase(109, true, false)]
    [TestCase(92, false, false)]
    public async Task RingOfHonorDropsOnlyForActiveQuestAsync(byte monsterLevel, bool activeQuest, bool expectedDrop)
    {
        var item = new BasicModel.ItemDefinition
        {
            Group = 14,
            Number = 23,
            IsActive = true,
            DropsFromMonsters = false,
            IsQuestItem = true,
            DropLevel = 0,
            MaximumItemLevel = 1,
            Durability = 1,
        };
        var questGroup = new BasicModel.DropItemGroup
        {
            Chance = 0.03,
            MinimumMonsterLevel = 72,
            MaximumMonsterLevel = 108,
            ItemLevel = 1,
        };
        questGroup.PossibleItems.Add(item);
        var quest = new BasicModel.QuestDefinition { Group = 0, Number = 2 };
        quest.RequiredItems.Add(new BasicModel.QuestItemRequirement { Item = item, MinimumNumber = 1, DropItemGroup = questGroup });

        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        Mock.Get(player.SelectedCharacter!).Setup(c => c.QuestStates).Returns(
            new List<CharacterQuestState> { new() { ActiveQuest = activeQuest ? quest : null } });
        player.CurrentMap!.Definition.DropItemGroups.Add(new BasicModel.DropItemGroup { Chance = 0.5, ItemType = SpecialItemType.Money });
        player.CurrentMap.Definition.DropItemGroups.Add(new BasicModel.DropItemGroup { Chance = 0.3, ItemType = SpecialItemType.RandomItem });

        var monster = new BasicModel.MonsterDefinition { NumberOfMaximumItemDrops = 1, ObjectKind = NpcObjectKind.Monster };
        monster.Attributes.Add(new BasicModel.MonsterAttribute { AttributeDefinition = Stats.Level, Value = monsterLevel });
        var randomizer = new Mock<IRandomizer>();
        randomizer.Setup(r => r.NextDouble()).Returns(0.81);
        randomizer.Setup(r => r.NextInt(It.IsAny<int>(), It.IsAny<int>())).Returns((int minimum, int _) => minimum);
        var config = new BasicModel.GameConfiguration();
        config.Items.Add(item);
        var generator = new DefaultDropGenerator(config, randomizer.Object);

        var (items, _) = await generator.GenerateItemDropsAsync(monster, 1, player).ConfigureAwait(false);
        var drops = items.ToList();
        Assert.That(drops, Has.Count.EqualTo(expectedDrop ? 1 : 0));
        if (expectedDrop)
        {
            Assert.That(drops[0].Definition, Is.SameAs(item));
            Assert.That(drops[0].Level, Is.EqualTo(1));
        }
    }
}
