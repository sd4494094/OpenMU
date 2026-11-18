// <copyright file="GuildDeletionSynchronizationTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Moq;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Views.Guild;
using MUnique.OpenMU.Interfaces;
using BasicModel = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Tests that deleting a guild clears and notifies its online members before removing their index.
/// </summary>
[TestFixture]
public class GuildDeletionSynchronizationTests
{
    /// <summary>
    /// A disband reaches the master and ordinary members, leaves other guilds alone, and is idempotent.
    /// </summary>
    [Test]
    public async ValueTask DisbandClearsOnlineMembershipAndNotifiesMembersAsync()
    {
        const uint deletedGuildId = 7;
        const uint otherGuildId = 8;
        var configuration = new BasicModel.GameConfiguration();
        var map = new BasicModel.GameMapDefinition { TerrainData = new byte[ushort.MaxValue + 3] };
        configuration.Maps.Add(map);
        await using var server = TestGameServers.Create(configuration);
        server.Context.ServerConfiguration.Maps.Add(map);
        var players = new List<Player>();
        try
        {
            foreach (var (guildId, role) in new[]
                     {
                         (deletedGuildId, GuildPosition.GuildMaster),
                         (deletedGuildId, GuildPosition.NormalMember),
                         (otherGuildId, GuildPosition.NormalMember),
                     })
            {
                var player = await PlayerTestHelper.CreatePlayerAsync(server.Context).ConfigureAwait(false);
                players.Add(player);
                player.GuildStatus = new GuildMemberStatus(guildId, role);
                await server.Context.RegisterGuildMemberAsync(player).ConfigureAwait(false);
            }

            await server.GuildDeletedAsync(deletedGuildId).ConfigureAwait(false);
            await server.GuildDeletedAsync(deletedGuildId).ConfigureAwait(false);

            foreach (var player in players.Take(2))
            {
                Assert.That(player.GuildStatus, Is.Null, "Disband must clear every online member, including the master.");
                Mock.Get(player.ViewPlugIns.GetPlugIn<IGuildKickResultPlugIn>()!)
                    .Verify(view => view.GuildKickResultAsync(GuildKickSuccess.KickSucceeded), Times.Once);
            }

            Assert.That(players[2].GuildStatus?.GuildId, Is.EqualTo(otherGuildId));
            Mock.Get(players[2].ViewPlugIns.GetPlugIn<IGuildKickResultPlugIn>()!)
                .Verify(view => view.GuildKickResultAsync(It.IsAny<GuildKickSuccess>()), Times.Never);
            var remainingDeletedMembers = new List<Player>();
            await server.Context.ForEachGuildPlayerAsync(deletedGuildId, player =>
            {
                remainingDeletedMembers.Add(player);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            Assert.That(remainingDeletedMembers, Is.Empty);
        }
        finally
        {
            foreach (var player in players)
            {
                await player.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
