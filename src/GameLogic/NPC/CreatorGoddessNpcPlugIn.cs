// <copyright file="CreatorGoddessNpcPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.NPC;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.Views.NPC;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Gives the decorative creator goddess a localized greeting without holding an NPC session.
/// </summary>
[PlugIn]
[Guid("073db507-0ca8-4b06-bf0d-8ca76aeeed01")]
[Display(Name = nameof(PlugInResources.CreatorGoddessNpc_Name), Description = nameof(PlugInResources.CreatorGoddessNpc_Description), ResourceType = typeof(PlugInResources))]
public class CreatorGoddessNpcPlugIn : NpcTalkPlugInBase
{
    /// <summary>
    /// The independent wire identity of Aria, matching the custom client's NPC definition.
    /// </summary>
    public const short NpcNumber = 711;

    /// <inheritdoc/>
    public override short DefaultNpcNumber => NpcNumber;

    /// <inheritdoc/>
    protected override async ValueTask PlayerTalksToConfiguredNpcAsync(Player player, NonPlayerCharacter npc, NpcTalkEventArgs eventArgs)
    {
        eventArgs.HasBeenHandled = true;
        eventArgs.LeavesDialogOpen = false;
        await player.InvokeViewPlugInAsync<IShowMessageOfObjectPlugIn>(view => view.ShowMessageOfObjectAsync(
            player.GetLocalizedMessage(nameof(Properties.PlayerMessage.CreatorGoddessGreeting)), npc)).ConfigureAwait(false);
    }
}
