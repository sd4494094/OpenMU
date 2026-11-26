// <copyright file="MapHostReadinessTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameServer;
using MUnique.OpenMU.Interfaces;

/// <summary>
/// Verifies the actual host is still available after its asynchronous map initialization.
/// </summary>
[TestFixture]
public class MapHostReadinessTests
{
    /// <summary>
    /// A host selected before stopping must not hand out its map after stopping.
    /// </summary>
    /// <param name="stopping">Whether the selected host stops while loading the map.</param>
    /// <returns>The task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task AvailabilityIsRecheckedAfterInitializationAsync(bool stopping)
    {
        var map = new GameMap(new GameMapDefinition { Number = 1 }, TimeSpan.FromMinutes(1), 8);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<GameMap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new Mock<IGameServerContext>();
        context.SetupGet(value => value.Id).Returns(1);
        context.Setup(value => value.GetMapAsync(1, true)).Returns(() =>
        {
            entered.TrySetResult();
            return new ValueTask<GameMap?>(release.Task);
        });
        var server = new Mock<IGameServer>();
        server.SetupGet(value => value.Id).Returns(1);
        var serverState = ServerState.Started;
        server.SetupGet(value => value.ServerState).Returns(() => serverState);
        server.As<IGameServerContextProvider>().SetupGet(value => value.Context).Returns(context.Object);
        var requester = new Mock<IGameServerContext>();
        requester.SetupGet(value => value.Id).Returns(2);
        var locator = new InProcessMapHostLocator(new Dictionary<int, IGameServer> { [1] = server.Object });
        var locating = locator.LocateAsync(requester.Object, 1).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (stopping)
        {
            serverState = ServerState.Stopping;
        }

        release.SetResult(map);
        var result = await locating.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (stopping)
        {
            Assert.That(result, Is.Null);
        }
        else
        {
            Assert.That(result!.Map, Is.SameAs(map));
        }

        await map.TearDownAsync().ConfigureAwait(false);
    }
}
