// <copyright file="ResetChatCommandPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Resets;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.PlugIns.ChatCommands;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// A chat command plugin which handles reset command.
/// </summary>
/// <seealso cref="MUnique.OpenMU.GameLogic.PlugIns.ChatCommands.IChatCommandPlugIn" />
[Guid("90B35404-AADE-4F22-B5D2-4CD59B8BB4C8")]
[PlugIn]
[Display(Name = nameof(PlugInResources.ResetChatCommandPlugIn_Name), Description = nameof(PlugInResources.ResetChatCommandPlugIn_Description), ResourceType = typeof(PlugInResources))]
[ChatCommandHelp(Command)]
public class ResetChatCommandPlugIn : IChatCommandPlugIn
{
    private const string Command = "/reset";

    /// <inheritdoc />
    public string Key => Command;

    /// <inheritdoc />
    public CharacterStatus MinCharacterStatusRequirement => CharacterStatus.Normal;

    /// <inheritdoc />
    public async ValueTask HandleCommandAsync(Player player, string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && parts[1] == "prepare")
        {
            await ResetNpcDialogAction.HandleAsync(player, "prepare").ConfigureAwait(false);
            return;
        }

        if (parts.Length == 3 && parts[1] == "confirm")
        {
            await ResetNpcDialogAction.HandleAsync(player, "confirm", parts[2]).ConfigureAwait(false);
            return;
        }

        if (parts.Length != 1)
        {
            return;
        }

        if (ResetNpcDialogAction.IsOpen(player))
        {
            await ResetNpcDialogAction.HandleAsync(player, "prepare").ConfigureAwait(false);
            return;
        }

        var resetAction = new ResetCharacterAction(player);
        await resetAction.ResetCharacterAsync().ConfigureAwait(false);
    }
}
