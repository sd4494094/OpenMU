// <copyright file="AriaAssistantChatCommandPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns.ChatCommands;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.Aria;
using MUnique.OpenMU.PlugIns;

/// <summary>The /小助理 alias of Aria's private conversation.</summary>
[PlugIn]
[Guid("a1cb5d3d-a45d-4744-a985-235c078a1621")]
[Display(Name = nameof(AriaResources.CommandName), Description = nameof(AriaResources.CommandDescription), ResourceType = typeof(AriaResources))]
[ChatCommandHelp("/小助理", CharacterStatus.Normal)]
public sealed class AriaAssistantChatCommandPlugIn : AriaChatCommandPlugInBase
{
    /// <summary>Initializes a new instance of the <see cref="AriaAssistantChatCommandPlugIn"/> class without host integration.</summary>
    public AriaAssistantChatCommandPlugIn()
        : this(null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AriaAssistantChatCommandPlugIn"/> class.</summary>
    /// <param name="service">The optional conversation service.</param>
    public AriaAssistantChatCommandPlugIn(IAriaChatService? service)
        : base(service)
    {
    }

    /// <inheritdoc />
    public override string Key => "/小助理";
}
