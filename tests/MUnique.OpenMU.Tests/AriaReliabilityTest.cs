// <copyright file="AriaReliabilityTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using MUnique.OpenMU.GameLogic.Aria;
using MUnique.OpenMU.Startup.Aria;

/// <summary>Regression coverage for natural chat lines and bounded model retries.</summary>
[TestFixture]
public class AriaReliabilityTest
{
    /// <summary>Preserves screenshot answers without leading closing punctuation.</summary>
    /// <param name="text">The completed answer.</param>
    [TestCase("目前没有本服已核实的洛克羽毛掉落资料，我还不能确定该打什么怪。你可以查看本服掉落说明，或向管理员确认。")]
    [TestCase("神谕的联系暂时中断了，请稍后再问我。")]
    [TestCase("通常去失落之塔，击杀任务范围内的怪物。先接任务，再留意掉落。")]
    [TestCase("中文English123，前往18,106附近，完成任务后再返回。")]
    [TestCase("你好，Will。愿勇者大陆的风伴你前行，若旅途中有什么疑问，都可以来找我。")]
    [TestCase("甲乙丙丁戊己庚辛壬癸甲乙丙丁戊。第二句。")]
    public void NaturalLinesPreserveTextAndPunctuation(string text)
    {
        var lines = AriaChatText.SplitForChat(text, "艾莉雅");
        Assert.That(string.Concat(lines), Is.EqualTo(text));
        foreach (var line in lines)
        {
            Assert.That("，。！？；：、,.!?;:）】》」』”’)]}>…", Does.Not.Contain(line[..1]));
            Assert.That(Encoding.UTF8.GetByteCount(line), Is.LessThanOrEqualTo(80));
            Assert.That(line.EnumerateRunes().Sum(rune => rune.Value > 0xFF ? 2 : 1) + 9, Is.LessThanOrEqualTo(40));
        }
    }

    /// <summary>Chooses clause boundaries and preserves a coordinate spanning the old cut.</summary>
    [Test]
    public void UsesNaturalBoundaryBeforeHardCut()
    {
        var lines = AriaChatText.SplitForChat("通常去失落之塔，击杀任务范围内的怪物。", "艾莉雅");
        Assert.That(lines[0], Is.EqualTo("通常去失落之塔，"));
        lines = AriaChatText.SplitForChat("甲乙丙丁戊己庚辛壬癸甲乙18,106附近。", "艾莉雅");
        Assert.That(lines.Any(line => line.Contains("18,106", StringComparison.Ordinal)), Is.True);
    }

    /// <summary>Retries temporary HTTP failures once, retaining the same model request.</summary>
    /// <param name="status">The temporary failure status.</param>
    [TestCase(500)]
    [TestCase(502)]
    [TestCase(503)]
    [TestCase(504)]
    public async Task RetriesTemporaryStatusOnceAsync(int status)
    {
        using var handler = new SequenceHandler((attempt, _) => Task.FromResult(attempt == 1
            ? new HttpResponseMessage((HttpStatusCode)status)
            : Answer()));
        using var http = new HttpClient(handler);
        var result = await Client(http).CompleteAsync("test", [new AriaMessage("user", "你好")], CancellationToken.None).ConfigureAwait(false);
        Assert.That(result, Is.EqualTo("你好，勇者。"));
        Assert.That(handler.Attempts, Is.EqualTo(2));
        Assert.That(handler.RequestBodies.Distinct().Count(), Is.EqualTo(1));
    }

    /// <summary>Never retries authentication, billing/ambiguous rate limits or invalid input.</summary>
    /// <param name="status">The permanent or ambiguous failure status.</param>
    [TestCase(400)]
    [TestCase(401)]
    [TestCase(403)]
    [TestCase(404)]
    [TestCase(429)]
    public void DoesNotRetryPermanentStatus(int status)
    {
        using var handler = new SequenceHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        using var http = new HttpClient(handler);
        var exception = Assert.ThrowsAsync<HttpRequestException>(async () =>
            await Client(http).CompleteAsync("test", [], CancellationToken.None).ConfigureAwait(false));
        Assert.That(exception!.StatusCode, Is.EqualTo((HttpStatusCode)status));
        Assert.That(handler.Attempts, Is.EqualTo(1));
    }

    /// <summary>Caps repeated failures at two attempts.</summary>
    [Test]
    public void PersistentFailureStopsAfterOneRetry()
    {
        using var handler = new SequenceHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)));
        using var http = new HttpClient(handler);
        Assert.ThrowsAsync<HttpRequestException>(async () =>
            await Client(http).CompleteAsync("test", [], CancellationToken.None).ConfigureAwait(false));
        Assert.That(handler.Attempts, Is.EqualTo(2));
    }

    /// <summary>A broken connection or truncated stream can recover without publishing partial output.</summary>
    /// <param name="failure">The transport failure mode.</param>
    [TestCase("connection")]
    [TestCase("io")]
    [TestCase("truncated")]
    public async Task RetriesBrokenTransportAsync(string failure)
    {
        using var handler = new SequenceHandler((attempt, _) =>
        {
            if (attempt == 1)
            {
                if (failure == "connection")
                {
                    return Task.FromException<HttpResponseMessage>(new HttpRequestException("connection reset"));
                }

                if (failure == "io")
                {
                    return Task.FromException<HttpResponseMessage>(new IOException("stream reset"));
                }

                return Task.FromResult(EventStream("data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n"));
            }

            return Task.FromResult(Answer());
        });
        using var http = new HttpClient(handler);
        Assert.That(await Client(http).CompleteAsync("test", [], CancellationToken.None).ConfigureAwait(false), Is.EqualTo("你好，勇者。"));
        Assert.That(handler.Attempts, Is.EqualTo(2));
    }

    /// <summary>Explicit model failures are not confused with a dropped network stream.</summary>
    /// <param name="type">The failure event type.</param>
    [TestCase("response.incomplete")]
    [TestCase("response.failed")]
    [TestCase("error")]
    public void DoesNotRetryExplicitModelFailure(string type)
    {
        using var handler = new SequenceHandler((_, _) => Task.FromResult(EventStream("data: {\"type\":\"" + type + "\"}\n\n")));
        using var http = new HttpClient(handler);
        Assert.ThrowsAsync<InvalidDataException>(async () =>
            await Client(http).CompleteAsync("test", [], CancellationToken.None).ConfigureAwait(false));
        Assert.That(handler.Attempts, Is.EqualTo(1));
    }

    /// <summary>Cancellation prevents another attempt, including while respecting Retry-After.</summary>
    [Test]
    public void CancellationDuringBackoffPreventsRetry()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        using var handler = new SequenceHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(10));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        Assert.CatchAsync<OperationCanceledException>(async () =>
            await Client(http).CompleteAsync("test", [], deadline.Token).ConfigureAwait(false));
        Assert.That(handler.Attempts, Is.EqualTo(1));
    }

    /// <summary>A cancelled call never starts a model request.</summary>
    [Test]
    public void AlreadyCancelledCallDoesNotSend()
    {
        using var handler = new SequenceHandler((_, _) => Task.FromResult(Answer()));
        using var http = new HttpClient(handler);
        Assert.CatchAsync<OperationCanceledException>(async () =>
            await Client(http).CompleteAsync("test", [], new CancellationToken(true)).ConfigureAwait(false));
        Assert.That(handler.Attempts, Is.Zero);
    }

    private static AriaModelClient Client(HttpClient http)
        => new(http, new AriaChatOptions { ApiKey = "test-key", Endpoint = "http://localhost/v1/responses" });

    private static HttpResponseMessage Answer()
        => EventStream("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"你好，勇者。\"}]}]}}\n\n");

    private static HttpResponseMessage EventStream(string text)
        => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/event-stream") };

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Func<int, CancellationToken, Task<HttpResponseMessage>> _callback;

        public SequenceHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> callback)
        {
            this._callback = callback;
        }

        public int Attempts { get; private set; }

        public List<string> RequestBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Attempts++;
            this.RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return await this._callback(this.Attempts, cancellationToken).ConfigureAwait(false);
        }
    }
}
