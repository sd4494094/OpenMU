// <copyright file="AcceptMapTransferAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.Interfaces;
using PlayerCharacter = MUnique.OpenMU.DataModel.Entities.Character;

/// <summary>Accepts a saved character on the game server that owns its destination map.</summary>
public sealed class AcceptMapTransferAction
{
    /// <summary>Claims an account registration and enters the world with the transferred character.</summary>
    /// <param name="player">The newly connected player.</param>
    /// <param name="credential">The single-use transfer credential.</param>
    /// <param name="characterName">The name of the character to transfer.</param>
    /// <returns>Whether the character entered the world.</returns>
    public async ValueTask<bool> AcceptAsync(Player player, Guid credential, string characterName)
    {
        if (player.GameContext is not IGameServerContext context
            || context.LoginServer is not IMapTransferLoginServer transfers
            || player.PlayerState.CurrentState != PlayerState.LoginScreen)
        {
            return false;
        }

        var (place, _) = context.TryReserveLoginPlace(context.ServerConfiguration.MaximumPlayers, null);
        using (place)
        {
            if (place is not null)
            {
                try
                {
                    if (await LoadCharacterAsync(player, context, transfers, credential, characterName).ConfigureAwait(false) is { } loaded)
                    {
                        await player.SetAccountAsync(loaded.Account).ConfigureAwait(false);
                        if (await player.PlayerState.TryAdvanceToAsync(PlayerState.CharacterSelection).ConfigureAwait(false))
                        {
                            await player.SetSelectedCharacterAsync(loaded.Character).ConfigureAwait(false);
                            if (player.PlayerState.CurrentState == PlayerState.EnteredWorld && player.CurrentMap is not null)
                            {
                                return true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    player.Logger.LogError(ex, "Could not load a transferred character.");
                }
            }
        }

        // Keep registration ownership until disconnect has completed the persistence teardown.
        await player.DisconnectAsync().ConfigureAwait(false);
        return false;
    }

    private static async ValueTask<(Account Account, PlayerCharacter Character)?> LoadCharacterAsync(
        Player player,
        IGameServerContext context,
        IMapTransferLoginServer transfers,
        Guid credential,
        string characterName)
    {
        await using var transition = await player.PlayerState.TryBeginAdvanceToAsync(PlayerState.Authenticated).ConfigureAwait(false);
        if (!transition.Allowed)
        {
            return null;
        }

        // A concurrent disconnect waits for this transition. It must see the registration even
        // if the database load or a later enter-world step fails.
        transition.Allowed = false;
        if (await transfers.ClaimMapTransferAsync(credential, characterName, context.Id).ConfigureAwait(false) is not { } accountName)
        {
            return null;
        }

        player.SetLoginServerRegistration(accountName);
        var account = await player.PersistenceContext.GetAccountByLoginNameAsync(accountName).ConfigureAwait(false);
        if (account is null || account.IsTemplate || account.State is AccountState.Banned or AccountState.TemporarilyBanned
            || (account.State is not (AccountState.GameMaster or AccountState.GameMasterInvisible)
                && account.GetFirstUnfulfilledRequirement(context.JoinRequirements, DateTime.UtcNow) is not null))
        {
            return null;
        }

        var character = account.Characters.FirstOrDefault(candidate => string.Equals(candidate.Name, characterName, StringComparison.Ordinal));
        if (character?.CurrentMap is not { IsActive: true } destination
            || await context.GetMapAsync((ushort)destination.Number).ConfigureAwait(false) is not { IsTornDown: false })
        {
            return null;
        }

        transition.Allowed = true;
        return (account, character);
    }
}
