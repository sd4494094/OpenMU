// <copyright file="AriaChatCommandPlugInBase.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns.ChatCommands;

using MUnique.OpenMU.GameLogic.Aria;
using MUnique.OpenMU.PlugIns;

/// <summary>Shares Aria aliases without waiting for the model in the packet handler.</summary>
public abstract class AriaChatCommandPlugInBase : IChatCommandPlugIn, IDisabledByDefault
{
    private readonly IAriaChatService? _service;

    /// <summary>Initializes a new instance of the <see cref="AriaChatCommandPlugInBase"/> class.</summary>
    /// <param name="service">The optional host integration; unsupported hosts remain safe.</param>
    protected AriaChatCommandPlugInBase(IAriaChatService? service)
    {
        this._service = service;
    }

    /// <inheritdoc />
    public abstract string Key { get; }

    /// <inheritdoc />
    public CharacterStatus MinCharacterStatusRequirement => CharacterStatus.Normal;

    /// <inheritdoc />
    public async ValueTask HandleCommandAsync(Player player, string command)
    {
        var separator = command.IndexOf(' ');
        var question = separator < 0 ? string.Empty : command[(separator + 1)..].Trim();
        var resource = question.Length == 0 ? "AriaUsage"
            : this._service?.TrySubmit(player, question) ?? (this._service is null ? "AriaUnavailable" : null);
        if (resource is not null)
        {
            await player.ShowLocalizedBlueMessageAsync(resource).ConfigureAwait(false);
        }
    }
}
