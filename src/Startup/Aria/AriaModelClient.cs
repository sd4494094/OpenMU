// <copyright file="AriaModelClient.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Startup.Aria;

using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>A bounded Responses tool loop which publishes only completed final text.</summary>
public sealed class AriaModelClient : IAriaModelClient
{
    private readonly HttpClient _http;
    private readonly AriaChatOptions _options;
    private readonly ILogger<AriaModelClient> _logger;
    private readonly IAriaToolClient? _tools;

    /// <summary>Initializes a new instance of the <see cref="AriaModelClient"/> class.</summary>
    /// <param name="http">The lifecycle-owned CPA HTTP client.</param>
    /// <param name="options">The host-local settings.</param>
    /// <param name="logger">The optional transport logger.</param>
    /// <param name="tools">The separately authenticated gameplay queries.</param>
    public AriaModelClient(HttpClient http, AriaChatOptions options, ILogger<AriaModelClient>? logger = null, IAriaToolClient? tools = null)
    {
        this._http = http;
        this._options = options;
        this._logger = logger ?? NullLogger<AriaModelClient>.Instance;
        this._tools = tools;
    }

    /// <summary>Reads successful terminal output without publishing deltas or tool preludes.</summary>
    /// <param name="stream">The event stream.</param>
    /// <param name="cancellationToken">The shared cancellation token.</param>
    /// <returns>The final text of a text-only response.</returns>
    public static async ValueTask<string> ReadCompletedAsync(Stream stream, CancellationToken cancellationToken)
        => ExtractText(await ReadOutputAsync(stream, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public ValueTask<string> CompleteAsync(string instructions, IReadOnlyList<AriaMessage> messages, CancellationToken cancellationToken)
        => this.CompleteCoreAsync(instructions, messages, this._options.Mcp.Enabled, cancellationToken);

    /// <inheritdoc />
    public ValueTask<string> CompleteSummaryAsync(string instructions, IReadOnlyList<AriaMessage> messages, CancellationToken cancellationToken)
        => this.CompleteCoreAsync(instructions, messages, false, cancellationToken);

    private static async ValueTask<JsonElement> ReadOutputAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var data = new StringBuilder();
        var total = 0;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            total += line.Length;
            if (total > 524288 || line.Length > 65536)
            {
                throw new InvalidDataException("Aria event stream exceeded its budget.");
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.AppendLine(line[5..].TrimStart());
            }
            else if (line.Length == 0 && data.Length > 0)
            {
                var output = ReadEvent(data.ToString());
                data.Clear();
                if (output is { } completed)
                {
                    return completed;
                }
            }
        }

        if (data.Length > 0 && ReadEvent(data.ToString()) is { } finalOutput)
        {
            return finalOutput;
        }

        throw new EndOfStreamException("Aria stream ended without a completed response.");
    }

    private static JsonElement? ReadEvent(string data)
    {
        if (data.Trim() == "[DONE]")
        {
            return null;
        }

        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var eventType) ? eventType.GetString() : null;
        if (type is "response.failed" or "response.incomplete" or "error")
        {
            throw new InvalidDataException("Aria response failed or was incomplete.");
        }

        if (type != "response.completed")
        {
            return null;
        }

        var response = root.GetProperty("response");
        if (response.GetProperty("status").GetString() != "completed")
        {
            throw new InvalidDataException("Aria terminal status was not completed.");
        }

        var output = response.GetProperty("output");
        if (output.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Aria output must be an array.");
        }

        return output.Clone();
    }

    private static string ExtractText(JsonElement output)
    {
        var text = new StringBuilder();
        foreach (var item in output.EnumerateArray())
        {
            if (item.GetProperty("type").GetString() != "message")
            {
                continue;
            }

            foreach (var content in item.GetProperty("content").EnumerateArray())
            {
                if (content.GetProperty("type").GetString() == "output_text")
                {
                    text.Append(content.GetProperty("text").GetString());
                }
            }
        }

        return text.Length > 0 ? text.ToString() : throw new InvalidDataException("Aria returned no answer.");
    }

    private static bool IsTransient(Exception exception)
        => exception switch
        {
            HttpRequestException http => http.StatusCode is null or HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout,
            IOException when exception is not InvalidDataException => true,
            _ => false,
        };

    private async ValueTask<string> CompleteCoreAsync(string instructions, IReadOnlyList<AriaMessage> messages, bool allowTools, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(this._options.TimeoutSeconds, 5, 60)));
        var token = deadline.Token;
        token.ThrowIfCancellationRequested();
        var input = messages.Select(message => JsonSerializer.SerializeToElement(new { role = message.Role, content = message.Content })).ToList();
        var usedCalls = 0;
        var remainingBytes = AriaGameplayTools.ResultBudget;
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        for (var round = 0; ; round++)
        {
            var canQuery = allowTools && round < AriaGameplayTools.MaxRounds && usedCalls < AriaGameplayTools.MaxCalls;
            var output = await this.SendAsync(instructions, input, allowTools, canQuery, token).ConfigureAwait(false);
            var calls = output.EnumerateArray().Where(item => item.GetProperty("type").GetString() == "function_call").ToArray();
            if (calls.Length == 0)
            {
                return ExtractText(output);
            }

            if (!canQuery || calls.Length > AriaGameplayTools.MaxCalls - usedCalls)
            {
                throw new InvalidDataException("Aria exceeded the tool loop budget.");
            }

            // Preserve completed reasoning/function items for stateless Responses continuation.
            input.AddRange(output.EnumerateArray().Select(item => item.Clone()));
            foreach (var call in calls)
            {
                var id = call.GetProperty("call_id").GetString();
                if (string.IsNullOrEmpty(id) || id.Length > 256 || !callIds.Add(id))
                {
                    throw new InvalidDataException("Aria tool call identifier is invalid.");
                }

                var result = "{\"error\":\"query_budget_exhausted\"}";
                if (usedCalls < AriaGameplayTools.MaxCalls)
                {
                    usedCalls++;
                    result = await this.QueryAsync(call, remainingBytes - ((AriaGameplayTools.MaxCalls - usedCalls) * 128), token).ConfigureAwait(false);
                }

                remainingBytes -= Encoding.UTF8.GetByteCount(result);
                input.Add(JsonSerializer.SerializeToElement(new { type = "function_call_output", call_id = id, output = result }));
            }
        }
    }

    private async ValueTask<string> QueryAsync(JsonElement call, int resultBudget, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var name = call.GetProperty("name").GetString() ?? string.Empty;
        var logName = "blocked";
        try
        {
            var argumentText = call.GetProperty("arguments").GetString();
            if (argumentText is null || argumentText.Length > 2048)
            {
                throw new InvalidDataException("Tool arguments exceeded their budget.");
            }

            using var document = JsonDocument.Parse(argumentText);
            var arguments = document.RootElement;
            AriaGameplayTools.Validate(name, arguments);
            logName = name;
            if (this._tools is null)
            {
                throw new InvalidOperationException("Gameplay queries are not registered.");
            }

            var result = await this._tools.CallAsync(name, arguments, cancellationToken).ConfigureAwait(false);
            var bounded = AriaGameplayTools.BoundResult(result, Math.Min(resultBudget, 8192));
            this._logger.LogInformation("Aria gameplay tool {Tool} completed in {ElapsedMs} ms ({Bytes} output bytes).", logName, stopwatch.ElapsedMilliseconds, Encoding.UTF8.GetByteCount(bounded));
            return bounded;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            this._logger.LogWarning("Aria gameplay tool {Tool} unavailable after {ElapsedMs} ms ({ErrorType}).", logName, stopwatch.ElapsedMilliseconds, exception.GetType().Name);
            return "{\"error\":\"query_unavailable_or_invalid\",\"verified\":false}";
        }
    }

    private async ValueTask<JsonElement> SendAsync(string instructions, List<JsonElement> input, bool withTools, bool canQuery, CancellationToken token)
    {
        var requestId = Guid.NewGuid();
        var stopwatch = Stopwatch.StartNew();
        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var retryDelay = TimeSpan.FromMilliseconds(Random.Shared.Next(300, 701));
            HttpStatusCode? status = null;
            try
            {
                var body = new Dictionary<string, object?>
                {
                    ["model"] = this._options.Model,
                    ["instructions"] = instructions,
                    ["reasoning"] = new { effort = this._options.ReasoningEffort },
                    ["stream"] = true,
                    ["store"] = false,
                    ["include"] = new[] { "reasoning.encrypted_content" },
                    ["max_output_tokens"] = this._options.MaxOutputTokens,
                    ["input"] = input,
                };
                if (withTools)
                {
                    body["tools"] = AriaGameplayTools.Definitions;
                    body["tool_choice"] = canQuery ? "auto" : "none";
                    body["parallel_tool_calls"] = false;
                }

                using var request = new HttpRequestMessage(HttpMethod.Post, this._options.Endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", this._options.ApiKey);
                request.Content = JsonContent.Create(body);
                using var response = await this._http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                status = response.StatusCode;
                if (!response.IsSuccessStatusCode)
                {
                    var retryAfter = response.Headers.RetryAfter;
                    var minimumDelay = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow);
                    if (minimumDelay > TimeSpan.Zero)
                    {
                        retryDelay += minimumDelay.Value;
                    }

                    throw new HttpRequestException("Aria upstream returned an unsuccessful HTTP status.", null, response.StatusCode);
                }

                if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
                {
                    throw new InvalidDataException("Aria upstream did not return an event stream.");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var output = await ReadOutputAsync(stream, token).ConfigureAwait(false);
                this._logger.LogInformation("Aria model request {RequestId} attempt {Attempt} completed in {ElapsedMs} ms.", requestId, attempt, stopwatch.ElapsedMilliseconds);
                return output;
            }
            catch (Exception exception) when (attempt == 1 && !token.IsCancellationRequested && IsTransient(exception))
            {
                this._logger.LogWarning("Aria model request {RequestId} attempt {Attempt} failed after {ElapsedMs} ms; retrying in {DelayMs} ms (HTTP {Status}, {ErrorType}).", requestId, attempt, stopwatch.ElapsedMilliseconds, retryDelay.TotalMilliseconds, (int?)status, exception.GetType().Name);
                await Task.Delay(retryDelay, token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                this._logger.LogWarning("Aria model request {RequestId} attempt {Attempt} stopped after {ElapsedMs} ms (HTTP {Status}, {ErrorType}).", requestId, attempt, stopwatch.ElapsedMilliseconds, (int?)status, exception.GetType().Name);
                throw;
            }
        }
    }
}
