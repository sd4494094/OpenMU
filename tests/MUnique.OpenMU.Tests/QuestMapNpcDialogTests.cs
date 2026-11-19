// <copyright file="QuestMapNpcDialogTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.Interfaces;

/// <summary>
/// Tests server handling of NPC dialogs shown directly by the client.
/// </summary>
[TestFixture]
internal class QuestMapNpcDialogTests
{
    /// <summary>
    /// Quest entrance NPCs remain available for confirmation without an unsupported-dialog message.
    /// </summary>
    /// <param name="number">The NPC number.</param>
    /// <param name="expectsWarning">Whether the dialog is unsupported.</param>
    /// <returns>The test task.</returns>
    [TestCase(407, false)]
    [TestCase(408, false)]
    [TestCase(999, true)]
    public async Task OpeningDialogReportsOnlyUnsupportedNpcsAsync(short number, bool expectsWarning)
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var definition = new Mock<MonsterDefinition>();
        definition.Object.Number = number;
        definition.Object.Designation = new LocalizedString("Test NPC");
        definition.Setup(npc => npc.ItemCraftings).Returns([]);
        var npc = new Mock<NonPlayerCharacter>(new MonsterSpawnArea(), definition.Object, player.CurrentMap!).Object;
        var messages = Mock.Get(player.ViewPlugIns.GetPlugIn<IShowMessagePlugIn>()!);
        messages.Invocations.Clear();

        await new TalkNpcAction().TalkToNpcAsync(player, npc).ConfigureAwait(false);

        Assert.That(player.OpenedNpc, Is.SameAs(npc));
        Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.EnteredWorld));
        messages.Verify(view => view.ShowMessageAsync(It.IsAny<string>(), MessageType.BlueNormal),
            expectsWarning ? Times.Once() : Times.Never());
    }
}
