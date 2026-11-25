// <copyright file="MapTransferApi.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.ServerClients;

/// <summary>Internal map-transfer invocation contract shared by the game and central hosts.</summary>
public static class MapTransferApi
{
    /// <summary>The Dapr invocation route.</summary>
    public const string Route = "login/map-transfer";

    /// <summary>The configuration key for the credential used by trusted game hosts.</summary>
    public const string TokenSetting = "OPENMU_MAP_TRANSFER_TOKEN";

    /// <summary>The header carrying the game host's credential.</summary>
    public const string TokenHeader = "X-OpenMU-Map-Transfer-Token";

    /// <summary>Arguments for a source host reserving a saved character.</summary>
    public sealed record BeginRequest(string AccountName, string CharacterName, byte SourceServerId, byte TargetServerId);

    /// <summary>Arguments for a target host claiming a client credential.</summary>
    public sealed record ClaimRequest(Guid Credential, string CharacterName, byte TargetServerId);

    /// <summary>Arguments for a source host abandoning a reservation.</summary>
    public sealed record CancelRequest(Guid Credential, byte SourceServerId);
}
