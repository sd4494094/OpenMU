// <copyright file="AriaMcpClient.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Startup.Aria;

using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using ModelContextProtocol.Client;

/// <summary>Uses the existing MCP SDK with a separate key, fixed endpoint and normal TLS validation.</summary>
public sealed class AriaMcpClient : IAriaToolClient
{
    private readonly HttpClient _http;
    private readonly AriaMcpOptions _options;

    /// <summary>Initializes a new instance of the <see cref="AriaMcpClient"/> class.</summary>
    /// <param name="http">The isolated MCP HTTP client.</param>
    /// <param name="options">The private host options.</param>
    public AriaMcpClient(HttpClient http, AriaChatOptions options)
    {
        this._http = http;
        this._options = options.Mcp;
    }

    /// <summary>Creates the bounded transport with optional custom CA and mandatory host verification.</summary>
    /// <param name="options">The private MCP settings.</param>
    /// <returns>The owning HTTP handler.</returns>
    public static HttpMessageHandler CreateHandler(AriaMcpOptions options)
    {
        var sockets = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false };
        if (!string.IsNullOrWhiteSpace(options.RootCertificateFile))
        {
            sockets.SslOptions = new SslClientAuthenticationOptions
            {
                CertificateChainPolicy = new X509ChainPolicy
                {
                    TrustMode = X509ChainTrustMode.CustomRootTrust,
                    CustomTrustStore = { X509Certificate2.CreateFromPem(File.ReadAllText(options.RootCertificateFile)) },
                    RevocationMode = X509RevocationMode.NoCheck,
                },
            };
        }

        // The configured server is stateless: each POST returns a finite JSON/SSE body.
        return new BoundedResponseHandler { InnerHandler = sockets };
    }

    /// <inheritdoc />
    public async ValueTask<JsonElement> CallAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        AriaGameplayTools.Validate(name, arguments);
        if (!this._options.Enabled)
        {
            throw new InvalidOperationException("Aria gameplay queries are disabled.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(this._options.TimeoutSeconds, 1, 10)));
        await using var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(this._options.Endpoint),
                Name = "Aria gameplay queries",
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + this._options.ApiKey },
            },
            this._http);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token).ConfigureAwait(false);
        var values = arguments.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
        if (name is "content_list" or "spawns_list" or "drops_explain" or "drops_find_sources")
        {
            values.TryAdd("limit", 10);
        }

        var result = await client.CallToolAsync(name, values, cancellationToken: timeout.Token).ConfigureAwait(false);
        if (result.IsError == true || result.StructuredContent is null)
        {
            // Never forward exception details, authentication headers or internal URLs to the model.
            throw new InvalidDataException("Gameplay query did not return a successful structured result.");
        }

        return JsonSerializer.SerializeToElement(result.StructuredContent);
    }

    private sealed class BoundedResponseHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            try
            {
                await response.Content.LoadIntoBufferAsync(131072, cancellationToken).ConfigureAwait(false);
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }
}
