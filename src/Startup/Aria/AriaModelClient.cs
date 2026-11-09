// <copyright file="AriaModelClient.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Startup.Aria;

using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;

/// <summary>A Responses client which publishes only successful completed output.</summary>
public sealed class AriaModelClient : IAriaModelClient
{
    private readonly HttpClient _http;
    private readonly AriaChatOptions _options;

    /// <summary>Initializes a new instance of the <see cref="AriaModelClient"/> class.</summary>
    /// <param name="http">The lifecycle-owned HTTP client.</param>
    /// <param name="options">The Aria settings.</param>
    public AriaModelClient(HttpClient http, AriaChatOptions options)
    {
        this._http = http;
        this._options = options;
    }

    /// <summary>Reads SSE until a successful terminal event, rejecting partial or incomplete output.</summary>
    /// <param name="stream">The event stream.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The completed response text.</returns>
    public static async ValueTask<string> ReadCompletedAsync(Stream stream, CancellationToken cancellationToken)
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
                var answer = ReadEvent(data.ToString());
                data.Clear();
                if (answer is not null)
                {
                    return answer;
                }
            }
        }

        if (data.Length > 0 && ReadEvent(data.ToString()) is { } finalAnswer)
        {
            return finalAnswer;
        }

        throw new InvalidDataException("Aria stream ended without a completed response.");
    }

    /// <inheritdoc />
    public async ValueTask<string> CompleteAsync(string instructions, IReadOnlyList<AriaMessage> messages, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, this._options.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", this._options.ApiKey);
        request.Content = JsonContent.Create(new
        {
            model = this._options.Model,
            instructions,
            reasoning = new { effort = this._options.ReasoningEffort },
            stream = true,
            max_output_tokens = this._options.MaxOutputTokens,
            input = messages.Select(message => new { role = message.Role, content = message.Content }),
        });
        using var response = await this._http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Never put upstream bodies, authorization headers or player questions in logs.
            throw new HttpRequestException("Aria upstream returned an unsuccessful HTTP status.", null, response.StatusCode);
        }

        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
        {
            throw new InvalidDataException("Aria upstream did not return an event stream.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await ReadCompletedAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static string? ReadEvent(string data)
    {
        if (data.Trim() == "[DONE]")
        {
            return null;
        }

        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : null;
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

        var text = new StringBuilder();
        foreach (var output in response.GetProperty("output").EnumerateArray())
        {
            if (output.GetProperty("type").GetString() != "message")
            {
                continue;
            }

            foreach (var content in output.GetProperty("content").EnumerateArray())
            {
                if (content.GetProperty("type").GetString() == "output_text")
                {
                    text.Append(content.GetProperty("text").GetString());
                }
            }
        }

        return text.Length > 0 ? text.ToString() : throw new InvalidDataException("Aria returned no answer.");
    }
}
