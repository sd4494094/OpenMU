// <copyright file="AriaLowerChatCommandPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns.ChatCommands;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.Aria;
using MUnique.OpenMU.PlugIns;

/// <summary>The /aria alias of Aria's private conversation.</summary>
[PlugIn]
[Guid("df439c86-d936-477e-a966-7ce3fa2c2b35")]
[Display(Name = nameof(AriaResources.CommandName), Description = nameof(AriaResources.CommandDescription), ResourceType = typeof(AriaResources))]
[ChatCommandHelp("/aria", CharacterStatus.Normal)]
public sealed class AriaLowerChatCommandPlugIn : AriaChatCommandPlugInBase
{
    /// <summary>Initializes a new instance of the <see cref="AriaLowerChatCommandPlugIn"/> class without host integration.</summary>
    public AriaLowerChatCommandPlugIn()
        : this(null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AriaLowerChatCommandPlugIn"/> class.</summary>
    /// <param name="service">The optional conversation service.</param>
    public AriaLowerChatCommandPlugIn(IAriaChatService? service)
        : base(service)
    {
    }

    /// <inheritdoc />
    public override string Key => "/aria";
}
