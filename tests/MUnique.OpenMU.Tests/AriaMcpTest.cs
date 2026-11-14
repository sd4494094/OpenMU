// <copyright file="AriaMcpTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MUnique.OpenMU.Startup.Aria;

/// <summary>Exercises the bounded Responses/MCP handoff and its independent authorization boundary.</summary>
[TestFixture]
public class AriaMcpTest
{
    /// <summary>Checks all six schemas and rejects every other capability.</summary>
    [Test]
    public void CatalogContainsExactlyTheApprovedTools()
    {
        var names = AriaGameplayTools.Definitions.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).ToArray();
        Assert.That(names, Is.EquivalentTo(new[] { "rates_get", "drops_explain", "drops_find_sources", "spawns_list", "content_list", "events_schedule" }));
    }

    /// <summary>Rejects malformed, oversized and privileged arguments before any remote request.</summary>
    /// <param name="name">The requested tool.</param>
    /// <param name="json">Its arguments.</param>
    [TestCase("config_get", "{}")]
    [TestCase("accounts_search", "{}")]
    [TestCase("rates_get", "{\"sql\":\"select 1\"}")]
    [TestCase("spawns_list", "{}")]
    [TestCase("spawns_list", "{\"mapNumber\":-1}")]
    [TestCase("spawns_list", "{\"mapNumber\":0,\"limit\":100}")]
    [TestCase("spawns_list", "{\"mapNumber\":0,\"mapNumber\":1}")]
    [TestCase("drops_explain", "{\"mapNumber\":0}")]
    [TestCase("content_list", "{\"type\":\"Accounts\"}")]
    [TestCase("content_list", "{\"type\":\"Items\",\"text\":\"bad\\nfilter\"}")]
    [TestCase("events_schedule", "{\"serverId\":32768}")]
    [TestCase("drops_find_sources", "{}")]
    [TestCase("drops_find_sources", "{\"text\":\" \"}")]
    [TestCase("drops_find_sources", "{\"text\":\"袭风\",\"itemLevel\":256}")]
    [TestCase("drops_find_sources", "{\"text\":\"袭风\",\"account\":\"other\"}")]
    [TestCase("rates_get", "[]")]
    public void InvalidQueryCannotReachMcp(string name, string json)
    {
        using var arguments = JsonDocument.Parse(json);
        using var handler = new Handler((_, _) => throw new AssertionException("Invalid query reached the network."));
        using var http = new HttpClient(handler);
        var client = new AriaMcpClient(http, Options());
        Assert.ThrowsAsync<InvalidDataException>(async () => await client.CallAsync(name, arguments.RootElement, CancellationToken.None).ConfigureAwait(false));
        Assert.That(handler.Bodies, Is.Empty);
    }

    /// <summary>Preserves reasoning, call identifiers and tool results across two query rounds.</summary>
    [Test]
    public async Task ContinuesWithVerifiedResultsWithoutLeakingKeysAsync()
    {
        var tools = new ToolClient((name, _) => JsonSerializer.SerializeToElement(new { source = name, total = 1, items = new[] { new { name = "雪球怪", quantity = 20 } } }));
        using var handler = new Handler((index, _) => Task.FromResult(index switch
        {
            1 => Output(new { type = "reasoning", id = "rs_1", encrypted_content = "opaque", summary = Array.Empty<object>() }, Call("content_list", "call_1", "{\"type\":\"Monsters\",\"text\":\"Yeti\"}")),
            2 => Output(Call("spawns_list", "call_2", "{\"mapNumber\":2,\"limit\":10}")),
            _ => Answer("本服配置中，这组怪物共20只。"),
        }));
        using var http = new HttpClient(handler);
        var result = await new AriaModelClient(http, Options(), tools: tools).CompleteAsync("role", [new AriaMessage("user", "这里多少怪？")], CancellationToken.None).ConfigureAwait(false);
        Assert.That(result, Is.EqualTo("本服配置中，这组怪物共20只。"));
        Assert.That(tools.Names, Is.EqualTo(new[] { "content_list", "spawns_list" }));
        using var second = JsonDocument.Parse(handler.Bodies[1]);
        var input = second.RootElement.GetProperty("input").EnumerateArray().ToArray();
        Assert.That(input.Any(item => item.TryGetProperty("encrypted_content", out var value) && value.GetString() == "opaque"), Is.True);
        var toolOutput = input.Single(item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
        Assert.That(toolOutput.GetProperty("call_id").GetString(), Is.EqualTo("call_1"));
        Assert.That(toolOutput.GetProperty("output").GetString(), Does.Contain("content_list"));
        Assert.That(string.Join("", handler.Bodies), Does.Not.Contain("mcp-only-secret").And.Not.Contain("https://mcp.invalid"));
        Assert.That(second.RootElement.GetProperty("store").GetBoolean(), Is.False);
        Assert.That(second.RootElement.GetProperty("reasoning").GetProperty("effort").GetString(), Is.EqualTo("low"));
    }

    /// <summary>An item question and a map follow-up reach the source query directly without spending rounds resolving ids.</summary>
    [Test]
    public async Task ItemSourceQuerySupportsChineseMapFollowUpAsync()
    {
        var tools = new ToolClient((name, _) =>
        {
            Assert.That(name, Is.EqualTo("drops_find_sources"));
            return JsonSerializer.SerializeToElement(new { status = "candidates", total = 1, items = new[] { new { map = "死亡沙漠", monster = "黑炎魔", requiresActiveQuest = false } } });
        });
        using var handler = new Handler((index, _) => Task.FromResult(index == 1
            ? Output(Call("drops_find_sources", "call_drop", "{\"text\":\"袭风刺\",\"map\":\"死亡沙漠\"}"))
            : Answer("死亡沙漠的黑炎魔有机会掉落袭风刺之石。")));
        using var http = new HttpClient(handler);
        var result = await new AriaModelClient(http, Options(), tools: tools).CompleteAsync("role", [new AriaMessage("user", "袭风刺技能石哪些怪掉"), new AriaMessage("assistant", "你想查哪张地图？"), new AriaMessage("user", "死亡沙漠")], CancellationToken.None).ConfigureAwait(false);
        Assert.That(result, Does.Contain("黑炎魔"));
        Assert.That(tools.Names, Is.EqualTo(new[] { "drops_find_sources" }));
        Assert.That(handler.Bodies[1], Does.Contain("function_call_output"));
    }

    /// <summary>Tool prelude text cannot be returned as the answer while a query is pending.</summary>
    [Test]
    public async Task PublishesOnlyTheFinalAnswerAsync()
    {
        var tools = new ToolClient((_, _) => JsonSerializer.SerializeToElement(new { rate = 100 }));
        using var handler = new Handler((index, _) => Task.FromResult(index == 1
            ? Output(new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = "稍等，我去查询。" } } }, Call("rates_get", "call_1", "{}"))
            : Answer("本服经验倍率是100倍。")));
        using var http = new HttpClient(handler);
        var answer = await new AriaModelClient(http, Options(), tools: tools).CompleteAsync("role", [], CancellationToken.None).ConfigureAwait(false);
        Assert.That(answer, Is.EqualTo("本服经验倍率是100倍。"));
    }

    /// <summary>Casual conversation makes no MCP requests, and summaries never advertise tools.</summary>
    [Test]
    public async Task CasualAndSummaryCallsDoNotQueryAsync()
    {
        var tools = new ToolClient((_, _) => throw new AssertionException("Unnecessary query."));
        using var handler = new Handler((_, _) => Task.FromResult(Answer("你好。")));
        using var http = new HttpClient(handler);
        var model = new AriaModelClient(http, Options(), tools: tools);
        await model.CompleteAsync("role", [new AriaMessage("user", "你好")], CancellationToken.None).ConfigureAwait(false);
        await model.CompleteSummaryAsync("summary", [], CancellationToken.None).ConfigureAwait(false);
        using var summary = JsonDocument.Parse(handler.Bodies[1]);
        Assert.That(summary.RootElement.TryGetProperty("tools", out _), Is.False);
        Assert.That(tools.Names, Is.Empty);
    }

    /// <summary>Turns tool errors into safe data and allows a truthful final answer.</summary>
    /// <param name="blocked">Whether the model requests a forbidden tool.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async Task FailedOrForbiddenQueriesNeverExposeExceptionDetailsAsync(bool blocked)
    {
        var tools = new ToolClient((_, _) => throw new HttpRequestException("mcp-only-secret https://mcp.invalid admin password"));
        using var handler = new Handler((index, _) => Task.FromResult(index == 1
            ? Output(Call(blocked ? "config_get" : "rates_get", "call_1", "{}"))
            : Answer("暂时查不到本服配置。")));
        using var http = new HttpClient(handler);
        var answer = await new AriaModelClient(http, Options(), tools: tools).CompleteAsync("role", [], CancellationToken.None).ConfigureAwait(false);
        Assert.That(answer, Is.EqualTo("暂时查不到本服配置。"));
        Assert.That(handler.Bodies[1], Does.Contain("query_unavailable_or_invalid").And.Not.Contain("mcp-only-secret"));
        Assert.That(tools.Names.Count, Is.EqualTo(blocked ? 0 : 1));
    }

    /// <summary>Three query rounds are followed by a final generation with tool_choice none.</summary>
    [Test]
    public async Task ExhaustedRoundsForceAnAnswerOnlyTurnAsync()
    {
        var tools = new ToolClient((_, _) => JsonSerializer.SerializeToElement(new { total = 0 }));
        using var handler = new Handler((index, _) => Task.FromResult(index <= 3 ? Output(Call("rates_get", "call_" + index, "{}")) : Answer("查询范围内尚无结果。")));
        using var http = new HttpClient(handler);
        await new AriaModelClient(http, Options(), tools: tools).CompleteAsync("role", [], CancellationToken.None).ConfigureAwait(false);
        Assert.That(tools.Names.Count, Is.EqualTo(3));
        using var final = JsonDocument.Parse(handler.Bodies[3]);
        Assert.That(final.RootElement.GetProperty("tool_choice").GetString(), Is.EqualTo("none"));
    }

    /// <summary>A non-compliant model cannot bypass the hard call cap through a batch.</summary>
    [Test]
    public void OversizedBatchIsRejectedWithoutQueries()
    {
        var tools = new ToolClient((_, _) => JsonSerializer.SerializeToElement(new { total = 0 }));
        using var handler = new Handler((_, _) => Task.FromResult(Output(Enumerable.Range(1, 5).Select(index => Call("rates_get", "call_" + index, "{}")).ToArray())));
        using var http = new HttpClient(handler);
        Assert.ThrowsAsync<InvalidDataException>(async () => await new AriaModelClient(http, Options(), tools: tools).CompleteAsync("role", [], CancellationToken.None).ConfigureAwait(false));
        Assert.That(tools.Names, Is.Empty);
    }

    /// <summary>Retrying the model's final HTTP response never repeats an already completed MCP query.</summary>
    [Test]
    public async Task FinalModelRetryDoesNotRepeatTheQueryAsync()
    {
        var tools = new ToolClient((_, _) => JsonSerializer.SerializeToElement(new { rate = 50 }));
        using var handler = new Handler((index, _) => Task.FromResult(index switch
        {
            1 => Output(Call("rates_get", "call_1", "{}")),
            2 => new HttpResponseMessage(HttpStatusCode.BadGateway),
            _ => Answer("本服经验50倍。"),
        }));
        using var http = new HttpClient(handler);
        var answer = await new AriaModelClient(http, Options(), tools: tools).CompleteAsync("role", [], CancellationToken.None).ConfigureAwait(false);
        Assert.That(answer, Is.EqualTo("本服经验50倍。"));
        Assert.That(tools.Names.Count, Is.EqualTo(1));
        Assert.That(handler.Bodies[1], Is.EqualTo(handler.Bodies[2]));
    }

    /// <summary>Cancellation during a query stops the conversation before another model generation.</summary>
    [Test]
    public void CancellationPreventsTheNextModelRequest()
    {
        using var cancellation = new CancellationTokenSource();
        var tools = new ToolClient((_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return default;
        });
        using var handler = new Handler((_, _) => Task.FromResult(Output(Call("rates_get", "call_1", "{}"))));
        using var http = new HttpClient(handler);
        Assert.CatchAsync<OperationCanceledException>(async () => await new AriaModelClient(http, Options(), tools: tools).CompleteAsync("role", [], cancellation.Token).ConfigureAwait(false));
        Assert.That(handler.Bodies.Count, Is.EqualTo(1));
    }

    /// <summary>Truncation preserves valid JSON, original total and an explicit warning.</summary>
    [Test]
    public void LargeResultIsBoundedWithoutFalseCompleteness()
    {
        var result = JsonSerializer.SerializeToElement(new { total = 100, items = Enumerable.Range(1, 100).Select(index => new { index, name = new string('怪', 50) }).ToArray() });
        var bounded = AriaGameplayTools.BoundResult(result, 1024);
        Assert.That(Encoding.UTF8.GetByteCount(bounded), Is.LessThanOrEqualTo(1024));
        using var json = JsonDocument.Parse(bounded);
        Assert.That(json.RootElement.GetProperty("total").GetInt32(), Is.EqualTo(100));
        Assert.That(json.RootElement.GetProperty("ariaTruncated").GetBoolean(), Is.True);
        Assert.That(json.RootElement.GetProperty("items").GetArrayLength(), Is.LessThan(100));
    }

    /// <summary>Production settings cannot send the gameplay credential over plaintext HTTP.</summary>
    [Test]
    public void RegistrationRejectsInsecureMcp()
    {
        var values = new Dictionary<string, string?>
        {
            ["Aria:Enabled"] = "true",
            ["Aria:Mcp:Enabled"] = "true",
            ["Aria:Mcp:Endpoint"] = "http://localhost/mcp",
            ["Aria:Mcp:ApiKey"] = "mcp-only-secret",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Assert.Throws<ArgumentException>(() => AriaServiceRegistration.AddAria(new ServiceCollection(), configuration));
    }

    /// <summary>Runs the real MCP SDK against a scripted JSON-RPC server and checks credential isolation.</summary>
    [Test]
    public async Task McpSdkPerformsHandshakeAndBoundedToolCallAsync()
    {
        using var handler = new Handler(async (_, request) =>
        {
            Assert.That(request.Headers.Authorization?.Parameter, Is.EqualTo("mcp-only-secret"));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync().ConfigureAwait(false));
            var root = body.RootElement;
            var method = root.GetProperty("method").GetString();
            if (method == "notifications/initialized")
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }

            var id = root.GetProperty("id").Clone();
            object result = method switch
            {
                "initialize" => new { protocolVersion = root.GetProperty("params").GetProperty("protocolVersion").GetString(), capabilities = new { tools = new { } }, serverInfo = new { name = "test", version = "1" } },
                "tools/list" => new { tools = Array.Empty<object>() },
                "tools/call" => new { content = Array.Empty<object>(), structuredContent = new { total = 1 }, isError = false },
                _ => throw new AssertionException("Unexpected MCP method " + method),
            };
            if (method == "tools/call")
            {
                Assert.That(root.GetProperty("params").GetProperty("name").GetString(), Is.EqualTo("spawns_list"));
                Assert.That(root.GetProperty("params").GetProperty("arguments").GetProperty("limit").GetInt32(), Is.EqualTo(10));
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }), Encoding.UTF8, "application/json") };
        });
        using var http = new HttpClient(handler);
        using var arguments = JsonDocument.Parse("{\"mapNumber\":2}");
        var result = await new AriaMcpClient(http, Options()).CallAsync("spawns_list", arguments.RootElement, CancellationToken.None).ConfigureAwait(false);
        Assert.That(result.GetProperty("total").GetInt32(), Is.EqualTo(1));
    }

    /// <summary>Loading a public root CA must never require access to its private key.</summary>
    [Test]
    public async Task CustomCaLoadsFromCertificateOnlyPemAsync()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Aria test root", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, certificate.ExportCertificatePem()).ConfigureAwait(false);
            using var handler = AriaMcpClient.CreateHandler(new AriaMcpOptions { RootCertificateFile = path });
            Assert.That(handler, Is.Not.Null);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static AriaChatOptions Options()
        => new()
        {
            ApiKey = "cpa-only-secret",
            Endpoint = "http://localhost/v1/responses",
            Mcp = new AriaMcpOptions { Enabled = true, Endpoint = "https://mcp.invalid/mcp", ApiKey = "mcp-only-secret" },
        };

    private static object Call(string name, string id, string arguments)
        => new { type = "function_call", call_id = id, name, arguments };

    private static HttpResponseMessage Answer(string text)
        => Output(new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text } } });

    private static HttpResponseMessage Output(params object[] output)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent("data: " + JsonSerializer.Serialize(new { type = "response.completed", response = new { status = "completed", output } }) + "\n\n", Encoding.UTF8, "text/event-stream"),
        };

    private sealed class ToolClient(Func<string, CancellationToken, JsonElement> callback) : IAriaToolClient
    {
        public List<string> Names { get; } = new();

        public ValueTask<JsonElement> CallAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
        {
            this.Names.Add(name);
            return ValueTask.FromResult(callback(name, cancellationToken));
        }
    }

    private sealed class Handler(Func<int, HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return await callback(this.Bodies.Count, request).ConfigureAwait(false);
        }
    }
}
