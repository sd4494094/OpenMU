// <copyright file="AriaMcpOptions.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Startup.Aria;

/// <summary>Private read-only gameplay connection, independent of the CPA model key.</summary>
public sealed class AriaMcpOptions
{
    /// <summary>Gets or sets a value indicating whether Aria can query gameplay tools.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the fixed HTTPS MCP endpoint.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Gets or sets the dedicated Viewer key with the five-tool allowlist.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional PEM root CA; normal host name verification remains enabled.</summary>
    public string? RootCertificateFile { get; set; }

    /// <summary>Gets or sets the per-query timeout within the existing conversation deadline.</summary>
    public int TimeoutSeconds { get; set; } = 6;
}
