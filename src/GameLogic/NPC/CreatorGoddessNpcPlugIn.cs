// <copyright file="CreatorGoddessNpcPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.NPC;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.Aria;
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

    private readonly IAriaChatService? _chatService;

    /// <summary>Initializes a new instance of the <see cref="CreatorGoddessNpcPlugIn"/> class.</summary>
    /// <param name="chatService">The optional, separately enabled AI conversation integration.</param>
    public CreatorGoddessNpcPlugIn(IAriaChatService? chatService = null)
    {
        this._chatService = chatService;
    }

    /// <inheritdoc/>
    public override short DefaultNpcNumber => NpcNumber;

    /// <inheritdoc/>
    protected override async ValueTask PlayerTalksToConfiguredNpcAsync(Player player, NonPlayerCharacter npc, NpcTalkEventArgs eventArgs)
    {
        eventArgs.HasBeenHandled = true;
        eventArgs.LeavesDialogOpen = false;
        await player.InvokeViewPlugInAsync<IShowMessageOfObjectPlugIn>(view => view.ShowMessageOfObjectAsync(
            player.GetLocalizedMessage(this._chatService is null ? nameof(Properties.PlayerMessage.CreatorGoddessGreeting) : "CreatorGoddessAiGreeting"), npc)).ConfigureAwait(false);
    }
}
