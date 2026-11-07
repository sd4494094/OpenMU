// <copyright file="LevelingWarpNpcPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.LevelingWarp;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Opens the dedicated menu only for a supported human client near the configured NPC.
/// </summary>
[PlugIn]
[Guid("3c98f104-83c4-4b5a-82b4-5ac6b054890a")]
[Display(Name = nameof(PlugInResources.LevelingWarpNpc_Name), Description = nameof(PlugInResources.LevelingWarpNpc_Description), ResourceType = typeof(PlugInResources))]
public class LevelingWarpNpcPlugIn : NpcTalkPlugInBase, IDisabledByDefault
{
    /// <inheritdoc/>
    public override short DefaultNpcNumber => 710;

    /// <inheritdoc/>
    protected override async ValueTask PlayerTalksToConfiguredNpcAsync(Player player, NonPlayerCharacter npc, NpcTalkEventArgs eventArgs)
    {
        eventArgs.HasBeenHandled = true;
        eventArgs.LeavesDialogOpen = await LevelingWarpAction.OpenAsync(player, npc).ConfigureAwait(false);
    }
}
