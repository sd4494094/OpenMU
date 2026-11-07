// <copyright file="LevelingWarpChatCommandPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.LevelingWarp;

using System.Globalization;
using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.PlugIns.ChatCommands;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Translates the menu selection; it never grants access without a current NPC session.
/// </summary>
[PlugIn]
[Guid("a72ea481-a95e-4af2-abdb-f189f4ca9525")]
[Display(Name = nameof(PlugInResources.LevelingWarpRequest_Name), Description = nameof(PlugInResources.LevelingWarpRequest_Description), ResourceType = typeof(PlugInResources))]
public class LevelingWarpChatCommandPlugIn : IChatCommandPlugIn
{
    /// <inheritdoc/>
    public string Key => "/levelwarp";

    /// <inheritdoc/>
    public CharacterStatus MinCharacterStatusRequirement => CharacterStatus.Normal;

    /// <inheritdoc/>
    public ValueTask HandleCommandAsync(Player player, string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is ["/levelwarp", "ready"])
        {
            LevelingWarpAction.RegisterClient(player);
            return ValueTask.CompletedTask;
        }

        return parts is ["/levelwarp", var token, var number]
            && token.Length == 32
            && int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? LevelingWarpAction.SelectAsync(player, token, id)
            : ValueTask.CompletedTask;
    }
}
