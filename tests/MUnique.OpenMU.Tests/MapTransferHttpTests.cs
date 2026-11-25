// <copyright file="MapTransferHttpTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Threading;
using global::Dapr.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.CentralServer.Host;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.ServerClients;

/// <summary>Exercises the map-transfer controller's service credential and result handling over HTTP.</summary>
[TestFixture]
public sealed class MapTransferHttpTests
{
    private const string TestToken = "test-only-map-transfer-token";

    /// <summary>All operations require the configured token before binding request data.</summary>
    /// <param name="operation">The name of the operation.</param>
    [TestCase(nameof(IMapTransferLoginServer.BeginMapTransferAsync))]
    [TestCase(nameof(IMapTransferLoginServer.ClaimMapTransferAsync))]
    [TestCase(nameof(IMapTransferLoginServer.CancelMapTransferAsync))]
    public async Task RejectsMissingWrongAndDuplicateTokensAsync(string operation)
    {
        var registry = new Mock<IMapTransferLoginServer>(MockBehavior.Strict);
        await using var app = await CreateHostAsync(registry.Object, TestToken).ConfigureAwait(false);
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        foreach (var tokens in new[] { Array.Empty<string>(), ["wrong"], new[] { TestToken, TestToken } })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/{MapTransferApi.Route}/{operation}")
            {
                Content = new StringContent("{", Encoding.UTF8, "application/json"),
            };
            if (tokens.Length != 0)
            {
                request.Headers.TryAddWithoutValidation(MapTransferApi.TokenHeader, tokens);
            }

            using var response = await http.SendAsync(request).ConfigureAwait(false);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        }

        Assert.That(registry.Invocations, Is.Empty);
    }

    /// <summary>A missing service token in the central host does not permit transfer calls.</summary>
    [Test]
    public async Task DisallowsRequestsWhenCentralTokenIsMissingAsync()
    {
        var registry = new Mock<IMapTransferLoginServer>(MockBehavior.Strict);
        await using var app = await CreateHostAsync(registry.Object, null).ConfigureAwait(false);
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        http.DefaultRequestHeaders.Add(MapTransferApi.TokenHeader, TestToken);
        using var response = await http.PostAsJsonAsync($"/{MapTransferApi.Route}/{nameof(IMapTransferLoginServer.BeginMapTransferAsync)}",
            new MapTransferApi.BeginRequest("account", "Character", 1, 2)).ConfigureAwait(false);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(registry.Invocations, Is.Empty);
    }

    /// <summary>An authorized host can claim a transfer and receive an explicit JSON null for refusal.</summary>
    [Test]
    public async Task AuthorizedClaimPreservesBothAccountAndRefusalResultsAsync()
    {
        var credential = Guid.NewGuid();
        var registry = new Mock<IMapTransferLoginServer>(MockBehavior.Strict);
        registry.Setup(server => server.ClaimMapTransferAsync(credential, "Character", 2)).ReturnsAsync("account");
        registry.Setup(server => server.ClaimMapTransferAsync(credential, "Character", 3)).ReturnsAsync((string?)null);
        await using var app = await CreateHostAsync(registry.Object, TestToken).ConfigureAwait(false);
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        http.DefaultRequestHeaders.Add(MapTransferApi.TokenHeader, TestToken);
        var route = $"/{MapTransferApi.Route}/{nameof(IMapTransferLoginServer.ClaimMapTransferAsync)}";
        using var allowed = await http.PostAsJsonAsync(route, new MapTransferApi.ClaimRequest(credential, "Character", 2)).ConfigureAwait(false);
        using var refused = await http.PostAsJsonAsync(route, new MapTransferApi.ClaimRequest(credential, "Character", 3)).ConfigureAwait(false);

        Assert.That(allowed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await allowed.Content.ReadFromJsonAsync<string>().ConfigureAwait(false), Is.EqualTo("account"));
        Assert.That(refused.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await refused.Content.ReadAsStringAsync().ConfigureAwait(false), Is.EqualTo("null"));
    }

    /// <summary>Runs the game-host client through the actual Dapr sidecar and Redis-backed controller.</summary>
    [Test]
    public async Task ReservationsRoundTripThroughDaprAsync()
    {
        var executable = Environment.GetEnvironmentVariable("OPENMU_TEST_DAPRD_PATH");
        var stateEndpoint = Environment.GetEnvironmentVariable("OPENMU_TEST_MAP_TRANSFER_DAPR_GRPC");
        if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(stateEndpoint))
        {
            Assert.Ignore("Set OPENMU_TEST_DAPRD_PATH and OPENMU_TEST_MAP_TRANSFER_DAPR_GRPC for the isolated Dapr test.");
        }

        using var state = new DaprClientBuilder().UseGrpcEndpoint(stateEndpoint!).Build();
        var login = new PersistentLoginServer(NullLogger<PersistentLoginServer>.Instance, state);
        await using var app = await CreateHostAsync(login, TestToken).ConfigureAwait(false);
        var ports = new HashSet<int>();
        while (ports.Count < 3)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            ports.Add(((IPEndPoint)listener.LocalEndpoint).Port);
        }

        var available = ports.Select(port => port.ToString(CultureInfo.InvariantCulture)).ToArray();
        var appPort = new Uri(app.Urls.Single()).Port.ToString(CultureInfo.InvariantCulture);
        var resources = Directory.CreateTempSubdirectory("openmu-map-transfer-http-");
        try
        {
            var start = new ProcessStartInfo(executable!) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in new[]
            {
                "--app-id", CentralServer.AppId, "--app-port", appPort, "--app-channel-address", "127.0.0.1",
                "--dapr-http-port", available[0], "--dapr-grpc-port", available[1], "--dapr-internal-grpc-port", available[2],
                "--dapr-listen-addresses", "127.0.0.1", "--resources-path", resources.FullName,
            })
            {
                start.ArgumentList.Add(arg);
            }

            using var sidecar = Process.Start(start) ?? throw new InvalidOperationException("Could not start daprd.");
            var diagnostics = Task.WhenAll(sidecar.StandardOutput.ReadToEndAsync(), sidecar.StandardError.ReadToEndAsync());
            try
            {
                var endpoint = $"http://127.0.0.1:{available[0]}";
                await WaitForSidecarAsync(sidecar, endpoint).ConfigureAwait(false);
                using var dapr = new DaprClientBuilder().UseHttpEndpoint(endpoint).Build();
                var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [MapTransferApi.TokenSetting] = TestToken,
                }).Build();
                IMapTransferLoginServer client = new ServerClients.LoginServer(dapr, NullLogger<ServerClients.LoginServer>.Instance, configuration);
                var account = Guid.NewGuid().ToString("N")[..10];
                Assert.That(await login.TryLoginAsync(account, 1).ConfigureAwait(false), Is.True);
                var credential = await client.BeginMapTransferAsync(account, "Character", 1, 2).ConfigureAwait(false);
                Assert.That(credential, Is.Not.Null);
                Assert.That(await client.ClaimMapTransferAsync(credential!.Value, "Character", 3).ConfigureAwait(false), Is.Null);
                Assert.That(await client.ClaimMapTransferAsync(credential.Value, "Character", 2).ConfigureAwait(false), Is.EqualTo(account));
                Assert.That(await client.ClaimMapTransferAsync(credential.Value, "Character", 2).ConfigureAwait(false), Is.Null);
                Assert.That(await client.CancelMapTransferAsync(credential.Value, 1).ConfigureAwait(false), Is.False);
                var returning = await client.BeginMapTransferAsync(account, "Character", 2, 1).ConfigureAwait(false);
                Assert.That(returning, Is.Not.Null);
                Assert.That(await client.CancelMapTransferAsync(returning!.Value, 1).ConfigureAwait(false), Is.False);
                Assert.That(await client.CancelMapTransferAsync(returning.Value, 2).ConfigureAwait(false), Is.True);
                Assert.That(await login.TryLoginAsync(account, 2).ConfigureAwait(false), Is.True);
                await login.LogOffAsync(account, 2).ConfigureAwait(false);
            }
            finally
            {
                if (!sidecar.HasExited)
                {
                    sidecar.Kill(entireProcessTree: true);
                }

                await sidecar.WaitForExitAsync().ConfigureAwait(false);
                var logs = await diagnostics.ConfigureAwait(false);
                TestContext.Progress.WriteLine(string.Join(Environment.NewLine, logs));
            }
        }
        finally
        {
            resources.Delete(recursive: true);
        }
    }

    private static async ValueTask WaitForSidecarAsync(Process sidecar, string endpoint)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var http = new HttpClient { BaseAddress = new Uri(endpoint) };
        while (!sidecar.HasExited)
        {
            try
            {
                using var health = await http.GetAsync("/v1.0/healthz", timeout.Token).ConfigureAwait(false);
                if (health.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The sidecar's API listener is still starting.
            }

            await Task.Delay(100, timeout.Token).ConfigureAwait(false);
        }

        Assert.Fail("Dapr exited before becoming ready.");
    }

    private static async ValueTask<WebApplication> CreateHostAsync(IMapTransferLoginServer registry, string? token)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [MapTransferApi.TokenSetting] = token });
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton<MapTransferAuthorizationFilter>();
        builder.Services.AddControllers().AddApplicationPart(typeof(MapTransferController).Assembly);
        var app = builder.Build();
        app.MapControllers();
        await app.StartAsync().ConfigureAwait(false);
        return app;
    }
}
