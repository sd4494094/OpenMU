// <copyright file="MapTransferDestinationTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using global::Dapr.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.Dapr.Common;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameServer.Host;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Network.PlugIns;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.BasicModel;
using MUnique.OpenMU.ServerClients;

/// <summary>Tests the receiving host's discovery endpoint and live listener checks.</summary>
[TestFixture]
public sealed class MapTransferDestinationTests
{
    private const string TestToken = "test-only-map-transfer-destination-token";
    private const byte GameServerId = 0;
    private const ushort TestMap = 0;
    private const ushort PublicPort = 45902;

    /// <summary>Only a started host with the requested map, configuration and client listener is advertised.</summary>
    [Test]
    public async Task AdvertisesOnlyAReadyCompatibleHostAsync()
    {
        var server = new Mock<IGameServer>();
        var state = ServerState.Started;
        server.SetupGet(s => s.ServerState).Returns(() => state);
        await using var context = LoginTestSetup.CreateGameServerContext(
            LoginTestSetup.CreatePersistence(Mock.Of<IPlayerContext>()), Mock.Of<ILoginServer>());
        context.StopPeriodicTasks();
        var configurationId = Guid.NewGuid();
        ((GameConfiguration)context.Configuration).Id = configurationId;
        var map = context.Configuration.Maps.Single();
        map.IsActive = true;
        using var publisher = new GameServerStatePublisher(Mock.Of<DaprClient>(), NullLogger<GameServerStatePublisher>.Instance);
        var endpoint = new GameServerEndpoint
        {
            NetworkPort = 30001,
            Client = new GameClientDefinition { Season = 106, Episode = 3, Language = ClientLanguage.English },
        };
        var observer = publisher.ForEndpoint(endpoint);
        observer.RegisterGameServer(new ServerInfo(GameServerId, "Target", 0, 10), new IPEndPoint(IPAddress.Parse("192.0.2.10"), PublicPort));

        await using var host = await CreateHostAsync(server.Object, context, publisher).ConfigureAwait(false);
        using var client = new HttpClient { BaseAddress = new Uri(host.Urls.Single()) };
        var route = $"/{MapTransferApi.DestinationRoute}";
        var request = new MapTransferApi.DestinationRequest(configurationId, TestMap, 106, 3, (byte)ClientLanguage.English);

        using var missingToken = await client.PostAsJsonAsync(route, request).ConfigureAwait(false);
        Assert.That(missingToken.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        client.DefaultRequestHeaders.Add(MapTransferApi.TokenHeader, TestToken);

        Assert.That(await GetDestinationAsync(client, route, request).ConfigureAwait(false),
            Is.EqualTo(new MapTransferApi.Destination(GameServerId, "192.0.2.10", PublicPort)));
        Assert.That(await GetDestinationAsync(client, route, request with { GameConfigurationId = Guid.NewGuid() }).ConfigureAwait(false), Is.Null);
        Assert.That(await GetDestinationAsync(client, route, request with { GameConfigurationId = Guid.Empty }).ConfigureAwait(false), Is.Null);
        Assert.That(await GetDestinationAsync(client, route, request with { MapNumber = 2 }).ConfigureAwait(false), Is.Null);
        Assert.That(await GetDestinationAsync(client, route, request with { Language = (byte)ClientLanguage.Japanese }).ConfigureAwait(false), Is.Null);

        map.IsActive = false;
        Assert.That(await GetDestinationAsync(client, route, request).ConfigureAwait(false), Is.Null);
        map.IsActive = true;
        state = ServerState.Stopping;
        Assert.That(await GetDestinationAsync(client, route, request).ConfigureAwait(false), Is.Null);
        state = ServerState.Started;

        observer.UnregisterGameServer(GameServerId);
        Assert.That(await GetDestinationAsync(client, route, request).ConfigureAwait(false), Is.Null);
    }

    private static async Task<MapTransferApi.Destination?> GetDestinationAsync(HttpClient client, string route, MapTransferApi.DestinationRequest request)
    {
        using var response = await client.PostAsJsonAsync(route, request).ConfigureAwait(false);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return await response.Content.ReadFromJsonAsync<MapTransferApi.Destination>().ConfigureAwait(false);
    }

    private static async ValueTask<WebApplication> CreateHostAsync(IGameServer server, IGameServerContext context, GameServerStatePublisher publisher)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [MapTransferApi.TokenSetting] = TestToken });
        builder.Services.AddSingleton(server);
        builder.Services.AddSingleton(context);
        builder.Services.AddSingleton(publisher);
        builder.Services.AddSingleton<MapTransferAuthorizationFilter>();
        builder.Services.AddControllers().AddApplicationPart(typeof(MapTransferDestinationController).Assembly);
        var app = builder.Build();
        app.MapControllers();
        await app.StartAsync().ConfigureAwait(false);
        return app;
    }
}
