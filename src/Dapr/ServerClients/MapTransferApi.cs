// <copyright file="MapTransferApi.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.ServerClients;

/// <summary>Internal map-transfer invocation contract shared by the game and central hosts.</summary>
public static class MapTransferApi
{
    /// <summary>The Dapr invocation route.</summary>
    public const string Route = "login/map-transfer";

    /// <summary>The game host route used to discover a compatible live listener.</summary>
    public const string DestinationRoute = "map-transfer/destination";

    /// <summary>The configuration key for the credential used by trusted game hosts.</summary>
    public const string TokenSetting = "OPENMU_MAP_TRANSFER_TOKEN";

    /// <summary>The header carrying the game host's credential.</summary>
    public const string TokenHeader = "X-OpenMU-Map-Transfer-Token";

    /// <summary>Identifies the shared game configuration, map and client version for a destination probe.</summary>
    public sealed record DestinationRequest(Guid GameConfigurationId, ushort MapNumber, byte Season, byte Episode, byte Language);

    /// <summary>The running destination game server and its client-facing IPv4 listener.</summary>
    public sealed record Destination(byte ServerId, string Address, ushort Port);

    /// <summary>Arguments for a source host reserving a saved character.</summary>
    public sealed record BeginRequest(string AccountName, string CharacterName, byte SourceServerId, byte TargetServerId);

    /// <summary>Arguments for a target host claiming a client credential.</summary>
    public sealed record ClaimRequest(Guid Credential, string CharacterName, byte TargetServerId);

    /// <summary>Arguments for a source host abandoning a reservation.</summary>
    public sealed record CancelRequest(Guid Credential, byte SourceServerId);
}
