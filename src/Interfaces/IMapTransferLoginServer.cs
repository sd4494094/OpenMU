// <copyright file="IMapTransferLoginServer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Interfaces;

/// <summary>Coordinates account ownership while a character switches game servers.</summary>
public interface IMapTransferLoginServer
{
    /// <summary>Replaces the source registration with a short-lived reservation.</summary>
    /// <param name="accountName">The canonical account name owned by the source.</param>
    /// <param name="characterName">The character whose progress was saved.</param>
    /// <param name="sourceServerId">The game server transferring the character.</param>
    /// <param name="targetServerId">The game server authorized to claim the reservation.</param>
    /// <returns>The single-use credential, or null if the reservation was refused.</returns>
    /// <remarks>The source must detach the character from gameplay and confirm its last save before calling.</remarks>
    ValueTask<Guid?> BeginMapTransferAsync(string accountName, string characterName, byte sourceServerId, byte targetServerId);

    /// <summary>Consumes the credential and establishes the target registration.</summary>
    /// <param name="credential">The credential presented on the target connection.</param>
    /// <param name="characterName">The exact character name presented on the target connection.</param>
    /// <param name="targetServerId">The game server accepting the connection.</param>
    /// <returns>The canonical account name registered at the target, or null if the claim was refused.</returns>
    ValueTask<string?> ClaimMapTransferAsync(Guid credential, string characterName, byte targetServerId);

    /// <summary>Cancels an unclaimed reservation and releases the account.</summary>
    /// <param name="credential">The credential issued to the source.</param>
    /// <param name="sourceServerId">The game server which issued the transfer.</param>
    /// <returns>Whether the reservation was canceled.</returns>
    ValueTask<bool> CancelMapTransferAsync(Guid credential, byte sourceServerId);
}
