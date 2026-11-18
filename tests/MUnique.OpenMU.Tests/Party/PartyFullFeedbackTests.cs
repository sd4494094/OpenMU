// <copyright file="PartyFullFeedbackTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.MuHelper;
using MUnique.OpenMU.GameLogic.PlayerActions.Party;
using MUnique.OpenMU.GameLogic.Views.Party;
using MUnique.OpenMU.Interfaces;

/// <summary>
/// Verifies capacity feedback through the invitation actions, including invitations which become stale.
/// </summary>
[TestFixture]
public class PartyFullFeedbackTests
{
    /// <summary>
    /// A full party informs its master immediately without asking another player to accept.
    /// </summary>
    [Test]
    public async ValueTask FullPartyRequestDoesNotInviteAsync()
    {
        await using var context = (GameContext)GameContextTestHelper.CreateGameContext();
        await using var party = await CreatePartyAsync(context, 5).ConfigureAwait(false);
        var requester = (Player)party.PartyMaster!;
        var receiver = await CreatePlayerAsync(context, "Receiver").ConfigureAwait(false);

        await new PartyRequestAction().HandlePartyRequestAsync(requester, receiver).ConfigureAwait(false);

        VerifyFull(requester, Times.Once());
        VerifyFull(receiver, Times.Never());
        Mock.Get(receiver.ViewPlugIns.GetPlugIn<IShowPartyRequestPlugIn>()!).Verify(p => p.ShowPartyRequestAsync(requester), Times.Never);
        Assert.That(receiver.LastPartyRequester, Is.Null);
        Assert.That(receiver.PlayerState.CurrentState, Is.EqualTo(PlayerState.EnteredWorld));
        Assert.That(party.PartyList, Has.Count.EqualTo(5));
    }

    /// <summary>
    /// A pending invitation can lose its vacancy before the receiver accepts it.
    /// </summary>
    [Test]
    public async ValueTask PendingInvitationBecomesFullNotifiesBothAsync()
    {
        await using var context = (GameContext)GameContextTestHelper.CreateGameContext();
        await using var party = await CreatePartyAsync(context, 4).ConfigureAwait(false);
        var requester = (Player)party.PartyMaster!;
        var receiver = await CreatePlayerAsync(context, "Receiver").ConfigureAwait(false);
        await new PartyRequestAction().HandlePartyRequestAsync(requester, receiver).ConfigureAwait(false);
        Assert.That(receiver.LastPartyRequester, Is.SameAs(requester));
        var other = await CreatePlayerAsync(context, "Other").ConfigureAwait(false);
        Assert.That(await party.AddAsync(other).ConfigureAwait(false), Is.True);

        await new PartyResponseAction().HandleResponseAsync(receiver, true).ConfigureAwait(false);

        Assert.That(receiver.Party, Is.Null);
        Assert.That(receiver.LastPartyRequester, Is.Null);
        Assert.That(receiver.PlayerState.CurrentState, Is.EqualTo(PlayerState.EnteredWorld));
        Assert.That(party.PartyList, Has.Count.EqualTo(5));
        VerifyFull(requester, Times.Once());
        VerifyFull(receiver, Times.Once());
    }

    /// <summary>
    /// A rejected receiver can be invited and accepted again once a member leaves.
    /// </summary>
    [Test]
    public async ValueTask RejectedReceiverCanJoinAfterVacancyAsync()
    {
        await using var context = (GameContext)GameContextTestHelper.CreateGameContext();
        await using var party = await CreatePartyAsync(context, 4).ConfigureAwait(false);
        var requester = (Player)party.PartyMaster!;
        var receiver = await CreatePlayerAsync(context, "Receiver").ConfigureAwait(false);
        var request = new PartyRequestAction();
        var response = new PartyResponseAction();
        await request.HandlePartyRequestAsync(requester, receiver).ConfigureAwait(false);
        var other = await CreatePlayerAsync(context, "Other").ConfigureAwait(false);
        Assert.That(await party.AddAsync(other).ConfigureAwait(false), Is.True);
        await response.HandleResponseAsync(receiver, true).ConfigureAwait(false);
        await new PartyKickAction().KickPlayerAsync(requester, 4).ConfigureAwait(false);

        await request.HandlePartyRequestAsync(requester, receiver).ConfigureAwait(false);
        await response.HandleResponseAsync(receiver, true).ConfigureAwait(false);

        Assert.That(receiver.Party, Is.SameAs(party));
        Assert.That(receiver.LastPartyRequester, Is.Null);
        Assert.That(party.PartyList, Has.Count.EqualTo(5));
        VerifyFull(requester, Times.Once());
        VerifyFull(receiver, Times.Once());
    }

    /// <summary>
    /// An ordinary invitation still succeeds without a capacity failure notification.
    /// </summary>
    [Test]
    public async ValueTask AvailablePartyAcceptsWithoutFullFeedbackAsync()
    {
        await using var context = (GameContext)GameContextTestHelper.CreateGameContext();
        await using var party = await CreatePartyAsync(context, 4).ConfigureAwait(false);
        var requester = (Player)party.PartyMaster!;
        var receiver = await CreatePlayerAsync(context, "Receiver").ConfigureAwait(false);
        await new PartyRequestAction().HandlePartyRequestAsync(requester, receiver).ConfigureAwait(false);

        await new PartyResponseAction().HandleResponseAsync(receiver, true).ConfigureAwait(false);

        Assert.That(receiver.Party, Is.SameAs(party));
        VerifyFull(requester, Times.Never());
        VerifyFull(receiver, Times.Never());
    }

    /// <summary>
    /// Concurrent acceptances for the final vacancy admit one receiver and notify the other.
    /// </summary>
    [Test]
    public async ValueTask TwoPendingInvitationsShareLastVacancyAsync()
    {
        await using var context = (GameContext)GameContextTestHelper.CreateGameContext();
        await using var party = await CreatePartyAsync(context, 4).ConfigureAwait(false);
        var requester = (Player)party.PartyMaster!;
        var first = await CreatePlayerAsync(context, "First").ConfigureAwait(false);
        var second = await CreatePlayerAsync(context, "Second").ConfigureAwait(false);
        var request = new PartyRequestAction();
        await request.HandlePartyRequestAsync(requester, first).ConfigureAwait(false);
        await request.HandlePartyRequestAsync(requester, second).ConfigureAwait(false);

        var response = new PartyResponseAction();
        await Task.WhenAll(response.HandleResponseAsync(first, true).AsTask(), response.HandleResponseAsync(second, true).AsTask()).ConfigureAwait(false);

        Assert.That(party.PartyList, Has.Count.EqualTo(5));
        Assert.That(new[] { first, second }.Count(p => p.Party == party), Is.EqualTo(1));
        foreach (var receiver in new[] { first, second })
        {
            VerifyFull(receiver, receiver.Party is null ? Times.Once() : Times.Never());
            Assert.That(receiver.LastPartyRequester, Is.Null);
            Assert.That(receiver.PlayerState.CurrentState, Is.EqualTo(PlayerState.EnteredWorld));
        }

        VerifyFull(requester, Times.Once());
    }

    /// <summary>
    /// Auto-accept by a full party master reports failure to both guild members.
    /// </summary>
    [Test]
    public async ValueTask FullReceivingPartyAutoAcceptReportsFailureAsync()
    {
        await using var context = (GameContext)GameContextTestHelper.CreateGameContext();
        await using var party = await CreatePartyAsync(context, 5).ConfigureAwait(false);
        var receiver = (Player)party.PartyMaster!;
        var requester = await CreatePlayerAsync(context, "Requester").ConfigureAwait(false);
        receiver.GuildStatus = new GuildMemberStatus(1, GuildPosition.GuildMaster);
        requester.GuildStatus = new GuildMemberStatus(1, GuildPosition.NormalMember);
        var settings = new Mock<IMuHelperSettings>();
        settings.Setup(s => s.AutoAcceptGuild).Returns(true);
        receiver.MuHelperSettings = settings.Object;

        await new PartyRequestAction().HandlePartyRequestAsync(requester, receiver).ConfigureAwait(false);

        Assert.That(requester.Party, Is.Null);
        Assert.That(receiver.Party, Is.SameAs(party));
        Assert.That(receiver.LastPartyRequester, Is.Null);
        Assert.That(party.PartyList, Has.Count.EqualTo(5));
        VerifyFull(requester, Times.Once());
        VerifyFull(receiver, Times.Once());
    }

    private static void VerifyFull(Player player, Times times)
    {
        Mock.Get(player.ViewPlugIns.GetPlugIn<IShowPartyFullPlugIn>()!).Verify(p => p.ShowPartyFullAsync(), times);
    }

    private static async ValueTask<Player> CreatePlayerAsync(IGameContext context, string name)
    {
        var player = await PlayerTestHelper.CreatePlayerAsync(context).ConfigureAwait(false);
        player.SelectedCharacter!.Name = name;
        await player.PlayerState.TryAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false);
        return player;
    }

    private static async ValueTask<Party> CreatePartyAsync(IGameContext context, int members)
    {
        var party = new Party(context.PartyManager, 5, NullLogger<Party>.Instance);
        for (var i = 0; i < members; i++)
        {
            var player = await CreatePlayerAsync(context, $"Member{i}").ConfigureAwait(false);
            Assert.That(await party.AddAsync(player).ConfigureAwait(false), Is.True);
        }

        return party;
    }
}
