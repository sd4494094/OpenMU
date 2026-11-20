// <copyright file="AvailableQuestEntriesTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameServer.RemoteView.Quest;
using MUnique.OpenMU.Network.Packets.ServerToClient;
using MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Tests the quest entries sent when an NPC offers several variants of one quest.
/// </summary>
[TestFixture]
public class AvailableQuestEntriesTests
{
    /// <summary>
    /// Variants with the same group and starting number have one entry.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task SharedStartingPagesAreListedOnceAsync()
    {
        var (player, output) = CastleSiegeRemoteViewTestHelper.CreatePlayer();
        var npc = new MonsterDefinition { Number = 257 };
        npc.Quests.Add(new QuestDefinition { Group = 19, StartingNumber = 3, Number = 5 });
        npc.Quests.Add(new QuestDefinition { Group = 19, StartingNumber = 3, Number = 6 });
        npc.Quests.Add(new QuestDefinition { Group = 20, StartingNumber = 3, Number = 5 });
        npc.Quests.Add(new QuestDefinition { Group = 19, StartingNumber = 48, Number = 50 });
        npc.Quests.Add(new QuestDefinition { Group = 19, StartingNumber = 93, Number = 95, MinimumCharacterLevel = 320 });
        player.OpenedNpc = new NonPlayerCharacter(null!, npc, null!);

        await new ShowAvailableQuestsPlugIn(player).ShowAvailableQuestsAsync().ConfigureAwait(false);

        var packet = (AvailableQuests)output.ToArray().AsMemory();
        Assert.That(packet.QuestNpcNumber, Is.EqualTo(257));
        Assert.That(packet.QuestCount, Is.EqualTo(3));
        Assert.That(Enumerable.Range(0, packet.QuestCount).Select(i => (packet[i].Group, packet[i].Number)),
            Is.EqualTo(new (ushort, ushort)[] { (19, 3), (20, 3), (19, 48) }));
    }
}
