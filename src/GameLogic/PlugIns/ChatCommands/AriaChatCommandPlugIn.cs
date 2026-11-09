// <copyright file="AriaChatCommandPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns.ChatCommands;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.Aria;
using MUnique.OpenMU.PlugIns;

/// <summary>The /Aria alias of Aria's private conversation.</summary>
[PlugIn]
[Guid("f6102b8b-b9e6-431a-a250-cb62d08f3811")]
[Display(Name = nameof(AriaResources.CommandName), Description = nameof(AriaResources.CommandDescription), ResourceType = typeof(AriaResources))]
[ChatCommandHelp("/Aria", CharacterStatus.Normal)]
public sealed class AriaChatCommandPlugIn : AriaChatCommandPlugInBase
{
    /// <summary>Initializes a new instance of the <see cref="AriaChatCommandPlugIn"/> class without host integration.</summary>
    public AriaChatCommandPlugIn()
        : this(null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AriaChatCommandPlugIn"/> class.</summary>
    /// <param name="service">The optional conversation service.</param>
    public AriaChatCommandPlugIn(IAriaChatService? service)
        : base(service)
    {
    }

    /// <inheritdoc />
    public override string Key => "/Aria";
}
