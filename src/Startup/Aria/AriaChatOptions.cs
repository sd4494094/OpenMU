// <copyright file="AriaChatOptions.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Startup.Aria;

/// <summary>Host-local settings for Aria only; not a global bot configuration.</summary>
public sealed class AriaChatOptions
{
    /// <summary>Gets or sets a value indicating whether the integration accepts requests.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the private CPA Responses endpoint.</summary>
    public string Endpoint { get; set; } = "http://cli-proxy-api:8317/v1/responses";

    /// <summary>Gets or sets the CPA client key, supplied by private configuration.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the configured CPA model alias.</summary>
    public string Model { get; set; } = "GPT-6.1 Terra";

    /// <summary>Gets or sets the default reasoning effort.</summary>
    public string ReasoningEffort { get; set; } = "low";

    /// <summary>Gets or sets the complete output budget, including reasoning.</summary>
    public int MaxOutputTokens { get; set; } = 1024;

    /// <summary>Gets or sets the prompt file path, relative to the application.</summary>
    public string PromptFile { get; set; } = "Aria/aria-system-prompt-v01.txt";

    /// <summary>Gets or sets an optional trusted server facts file.</summary>
    public string? KnowledgeFile { get; set; }

    /// <summary>Gets or sets the number of workers shared by all local game servers.</summary>
    public int Concurrency { get; set; } = 2;

    /// <summary>Gets or sets the maximum queued requests.</summary>
    public int QueueCapacity { get; set; } = 8;

    /// <summary>Gets or sets the end-to-end request timeout.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Gets or sets the per-character cooldown.</summary>
    public int CooldownSeconds { get; set; } = 3;

    /// <summary>Gets or sets the separate read-only gameplay connection.</summary>
    public AriaMcpOptions Mcp { get; set; } = new();

    /// <summary>Gets or sets raw conversation retention.</summary>
    public int RetentionDays { get; set; } = 30;
}
