// <copyright file="AriaChineseChatCommandPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns.ChatCommands;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.Aria;
using MUnique.OpenMU.PlugIns;

/// <summary>The /艾莉雅 alias of Aria's private conversation.</summary>
[PlugIn]
[Guid("57fcd12d-7b02-496a-963c-0d13ae28e7af")]
[Display(Name = nameof(AriaResources.CommandName), Description = nameof(AriaResources.CommandDescription), ResourceType = typeof(AriaResources))]
[ChatCommandHelp("/艾莉雅", CharacterStatus.Normal)]
public sealed class AriaChineseChatCommandPlugIn : AriaChatCommandPlugInBase
{
    /// <summary>Initializes a new instance of the <see cref="AriaChineseChatCommandPlugIn"/> class without host integration.</summary>
    public AriaChineseChatCommandPlugIn()
        : this(null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AriaChineseChatCommandPlugIn"/> class.</summary>
    /// <param name="service">The optional conversation service.</param>
    public AriaChineseChatCommandPlugIn(IAriaChatService? service)
        : base(service)
    {
    }

    /// <inheritdoc />
    public override string Key => "/艾莉雅";
}
