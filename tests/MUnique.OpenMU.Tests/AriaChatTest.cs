// <copyright file="AriaChatTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Aria;
using MUnique.OpenMU.GameLogic.PlugIns.ChatCommands;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.Aria;
using MUnique.OpenMU.Startup.Aria;

/// <summary>Verifies Aria's transport, aliases, isolation and asynchronous player integration.</summary>
[TestFixture]
public class AriaChatTest
{
    /// <summary>Checks the actual serialized upstream model and reasoning parameters.</summary>
    [Test]
    public async Task UsesLowReasoningAndWaitsForCompleteOutputAsync()
    {
        string? body = null;
        var handler = new TestHandler(async request =>
        {
            Assert.That(request.Headers.Authorization?.Scheme, Is.EqualTo("Bearer"));
            body = await request.Content!.ReadAsStringAsync().ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(StreamAnswer("你好，勇者。"), Encoding.UTF8, "text/event-stream"),
            };
        });
        using var http = new HttpClient(handler);
        var options = new AriaChatOptions { ApiKey = "test-key", Endpoint = "http://localhost/v1/responses" };
        var answer = await new AriaModelClient(http, options).CompleteAsync(
            "role instructions", [new AriaMessage("user", "你好")], CancellationToken.None).ConfigureAwait(false);
        Assert.That(answer, Is.EqualTo("你好，勇者。"));
        using var document = System.Text.Json.JsonDocument.Parse(body!);
        Assert.That(document.RootElement.GetProperty("reasoning").GetProperty("effort").GetString(), Is.EqualTo("low"));
        Assert.That(document.RootElement.GetProperty("model").GetString(), Is.EqualTo("GPT-6.1 Terra"));
        Assert.That(document.RootElement.GetProperty("instructions").GetString(), Is.EqualTo("role instructions"));
    }

    /// <summary>Rejects partial output, failed events and incomplete answers.</summary>
    /// <param name="terminal">The stream terminal data.</param>
    [TestCase("")]
    [TestCase("data: {\"type\":\"response.incomplete\"}\n\n")]
    [TestCase("data: {\"type\":\"response.failed\"}\n\n")]
    [TestCase("data: [DONE]\n\n")]
    public void PartialStreamIsNeverAnAnswer(string terminal)
    {
        var text = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"不能发送这段\"}\n\n" + terminal;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        Assert.ThrowsAsync<InvalidDataException>(async () =>
            await AriaModelClient.ReadCompletedAsync(stream, CancellationToken.None).ConfigureAwait(false));
    }

    /// <summary>All aliases delegate to the same service with normal player permissions.</summary>
    [Test]
    public async Task AliasesShareTheConversationServiceAsync()
    {
        var service = new Mock<IAriaChatService>();
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var services = new ServiceCollection();
        services.AddSingleton<IAriaChatService>(service.Object);
        using var provider = services.BuildServiceProvider();
        IChatCommandPlugIn[] plugins =
        [
            ActivatorUtilities.CreateInstance<AriaChatCommandPlugIn>(provider),
            ActivatorUtilities.CreateInstance<AriaLowerChatCommandPlugIn>(provider),
            ActivatorUtilities.CreateInstance<AriaChineseChatCommandPlugIn>(provider),
            ActivatorUtilities.CreateInstance<AriaAssistantChatCommandPlugIn>(provider),
        ];
        foreach (var plugin in plugins)
        {
            Assert.That(ChatCommandTypeExtensions.TryCreateChatCommandInfo(plugin.GetType()), Is.Not.Null);
            Assert.That(plugin.MinCharacterStatusRequirement, Is.EqualTo(DataModel.Entities.CharacterStatus.Normal));
            await plugin.HandleCommandAsync(player, plugin.Key + " 你好").ConfigureAwait(false);
        }

        service.Verify(value => value.TrySubmit(player, "你好"), Times.Exactly(4));
    }

    /// <summary>Bounds long and unusual Unicode output without malformed UTF-8 or client prefixes.</summary>
    [Test]
    public void ChineseAnswersFitOneChatPacket()
    {
        var input = "@@~$#`**\n" + new string('中', 200) + "😀";
        var result = AriaChatText.Constrain(input);
        Assert.That(result.Length, Is.LessThanOrEqualTo(80));
        Assert.That(Encoding.UTF8.GetByteCount(result), Is.LessThanOrEqualTo(240));
        Assert.That(result, Does.Not.Contain("\n").And.Not.Contain("@").And.Not.Contain("😀"));
        var strict = new UTF8Encoding(false, true);
        Assert.That(strict.GetString(strict.GetBytes(result)), Is.EqualTo(result));
        Assert.That(AriaChatText.Constrain(new string('中', 80)), Is.EqualTo(new string('中', 80)));
    }

    /// <summary>Preserves complete mixed-language content within narrow chat lines and byte buffers.</summary>
    /// <param name="text">The message.</param>
    /// <param name="sender">The sender label.</param>
    [TestCase("你好，Will。愿勇者大陆的风伴你前行，若旅途中有什么疑问，或只是想找人聊天，都可以来找我。", "艾莉雅")]
    [TestCase("@@测试聊天前缀；" + "中文English123，中文English123，中文English123，中文English123。", "LongName10")]
    [TestCase("甲乙e\u0301丙丁戊己庚辛壬癸甲乙丙丁戊己庚辛壬癸", "艾莉雅")]
    public void CompletedMessagesFitDisplayLines(string text, string sender)
    {
        var lines = AriaChatText.SplitForChat(text, sender);
        Assert.That(lines.Count, Is.GreaterThan(1));
        Assert.That(string.Concat(lines), Is.EqualTo(text));
        foreach (var line in lines)
        {
            Assert.That(Encoding.UTF8.GetByteCount(line), Is.LessThanOrEqualTo(80));
            var width = (line + sender).EnumerateRunes().Sum(rune => rune.Value > 0xFF ? 2 : 1) + 3;
            Assert.That(width, Is.LessThanOrEqualTo(40));
            Assert.That(char.IsLowSurrogate(line[0]), Is.False);
            Assert.That(char.GetUnicodeCategory(line[0]), Is.Not.EqualTo(System.Globalization.UnicodeCategory.NonSpacingMark));
        }
    }

    /// <summary>Rejected requests do not masquerade as submitted questions.</summary>
    [Test]
    public async Task RejectedQuestionsAreNotEchoedAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var service = new Mock<IAriaChatService>();
        service.Setup(value => value.TrySubmit(player, It.IsAny<string>())).Returns("AriaBusy");
        await new AriaChatCommandPlugIn(service.Object).HandleCommandAsync(player, "/Aria 你好").ConfigureAwait(false);
        Mock.Get(player.ViewPlugIns.GetPlugIn<IChatViewPlugIn>()!).Verify(
            view => view.ChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ChatMessageType>()), Times.Never());
    }

    /// <summary>A blocked model never holds the player's exclusive lock and only replies to its owner.</summary>
    [Test]
    public async Task WaitingModelDoesNotBlockGameAndReplyIsPrivateAsync()
    {
        var player = await CreateIdentifiedPlayerAsync().ConfigureAwait(false);
        var stranger = await PlayerTestHelper.CreatePlayerAsync(player.GameContext).ConfigureAwait(false);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new Mock<IAriaModelClient>();
        model.Setup(value => value.CompleteAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<AriaMessage>>(), It.IsAny<CancellationToken>()))
            .Returns((string _, IReadOnlyList<AriaMessage> _, CancellationToken token) =>
            {
                requested.TrySetResult();
                return new ValueTask<string>(complete.Task.WaitAsync(token));
            });
        var store = CreateStore();
        var prompt = Path.GetTempFileName();
        await File.WriteAllTextAsync(prompt, "You are Aria. Give a short private answer.").ConfigureAwait(false);
        using var service = new AriaChatService(new AriaChatOptions { Enabled = true, ApiKey = "test-key", PromptFile = prompt }, store.Object, model.Object, NullLogger<AriaChatService>.Instance);
        try
        {
            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await WaitReadyAsync(service).ConfigureAwait(false);
            await player.RunPersistenceExclusiveAsync(() =>
            {
                return new AriaChatCommandPlugIn(service).HandleCommandAsync(player, "/Aria 你好");
            }).ConfigureAwait(false);
            await requested.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await player.RunPersistenceExclusiveAsync(() => ValueTask.CompletedTask).AsTask().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            Assert.That(service.TrySubmit(player, "重复请求"), Is.EqualTo("AriaBusy"));
            Mock.Get(player.ViewPlugIns.GetPlugIn<IChatViewPlugIn>()!).Verify(
                view => view.ChatMessageAsync(It.Is<string>(text => text.EndsWith("你好", StringComparison.Ordinal)), player.Name, ChatMessageType.Normal), Times.Once());
            const string fullAnswer = "你好，Will。愿勇者大陆的风伴你前行，若旅途中有什么疑问，或只是想找人聊天，都可以来找我。";
            var expected = AriaChatText.SplitForChat(fullAnswer, player.GetLocalizedMessage("AriaSender"));
            complete.SetResult(fullAnswer);
            var chat = Mock.Get(player.ViewPlugIns.GetPlugIn<IChatViewPlugIn>()!);
            await WaitForAsync(() => chat.Invocations.Count >= expected.Count + 1).ConfigureAwait(false);
            var replies = chat.Invocations.Skip(1).Select(call => (string)call.Arguments[0]).ToArray();
            Assert.That(replies, Is.EqualTo(expected));
            Assert.That(string.Concat(replies), Is.EqualTo(fullAnswer));
            store.Verify(value => value.AppendAsync(
                It.IsAny<Guid>(), "aria", "你好", fullAnswer, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once());
            Mock.Get(stranger.ViewPlugIns.GetPlugIn<IChatViewPlugIn>()!).Verify(
                view => view.ChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ChatMessageType>()), Times.Never());
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).ConfigureAwait(false);
            File.Delete(prompt);
        }
    }

    /// <summary>Leaving the character cancels an in-flight answer, even if the player connection remains.</summary>
    [Test]
    public async Task LeavingCharacterCancelsOldReplyAsync()
    {
        var player = await CreateIdentifiedPlayerAsync().ConfigureAwait(false);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new Mock<IAriaModelClient>();
        model.Setup(value => value.CompleteAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<AriaMessage>>(), It.IsAny<CancellationToken>()))
            .Returns((string _, IReadOnlyList<AriaMessage> _, CancellationToken token) =>
            {
                requested.TrySetResult();
                return new ValueTask<string>(WaitUntilCanceledAsync(token));
            });
        var prompt = Path.GetTempFileName();
        await File.WriteAllTextAsync(prompt, "You are Aria. Give a short private answer.").ConfigureAwait(false);
        var store = CreateStore();
        using var service = new AriaChatService(new AriaChatOptions { Enabled = true, ApiKey = "test-key", PromptFile = prompt }, store.Object, model.Object, NullLogger<AriaChatService>.Instance);
        try
        {
            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await WaitReadyAsync(service).ConfigureAwait(false);
            Assert.That(service.TrySubmit(player, "你好"), Is.Null);
            await requested.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await player.RunPersistenceExclusiveAsync(() => player.SetSelectedCharacterAsync(null)).ConfigureAwait(false);
            await service.StopAsync(CancellationToken.None).ConfigureAwait(false);
            store.Verify(value => value.AppendAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
            Mock.Get(player.ViewPlugIns.GetPlugIn<IChatViewPlugIn>()!).Verify(
                view => view.ChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ChatMessageType>()), Times.Never());
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).ConfigureAwait(false);
            File.Delete(prompt);
        }
    }

    private static string StreamAnswer(string answer)
        => "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n"
            + "data: " + System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "response.completed",
                response = new
                {
                    status = "completed",
                    output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = answer } } } },
                },
            }) + "\n\n";

    private static Mock<IAriaMemoryStore> CreateStore()
    {
        var store = new Mock<IAriaMemoryStore>();
        store.Setup(value => value.LoadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AriaMemory(string.Empty, 0, [], []));
        store.Setup(value => value.AppendAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        return store;
    }

    private static async ValueTask<Player> CreateIdentifiedPlayerAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var template = player.SelectedCharacter!;
        var characterMock = new Mock<DataModel.Entities.Character>();
        var id = Guid.NewGuid();
        characterMock.SetupAllProperties();
        characterMock.As<Persistence.IIdentifiable>().SetupGet(value => value.Id).Returns(id);
        characterMock.SetupGet(value => value.Attributes).Returns(template.Attributes);
        characterMock.SetupGet(value => value.LearnedSkills).Returns(template.LearnedSkills);
        characterMock.SetupGet(value => value.DropItemGroups).Returns(template.DropItemGroups);
        characterMock.SetupGet(value => value.QuestStates).Returns(new List<DataModel.Entities.CharacterQuestState>());
        var character = characterMock.Object;
        character.Name = "AriaTest";
        character.CharacterClass = template.CharacterClass;
        character.CurrentMap = template.CurrentMap;
        character.Inventory = template.Inventory;
        await player.SetSelectedCharacterAsync(character).ConfigureAwait(false);
        Assert.That(player.SelectedCharacter?.GetId(), Is.EqualTo(id));
        return player;
    }

    private static async Task<string> WaitUntilCanceledAsync(CancellationToken token)
    {
        await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        return "must not be delivered";
    }

    private static async Task WaitReadyAsync(AriaChatService service)
        => await WaitForAsync(() => service.IsReady).ConfigureAwait(false);

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token).ConfigureAwait(false);
        }
    }

    private sealed class TestHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _callback;

        public TestHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback)
        {
            this._callback = callback;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => this._callback(request);
    }
}
