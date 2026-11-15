// <copyright file="AllowChineseCharacterNamesPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Enables Han character names when the operator still uses the original ASCII-only name policy.
/// </summary>
[PlugIn]
[Guid("4fe5f9d5-da17-4ba8-994b-fe4d9bc0b873")]
[Display(Name = nameof(Properties.CharacterNameUpdateResources.Name), Description = nameof(Properties.CharacterNameUpdateResources.Description), ResourceType = typeof(Properties.CharacterNameUpdateResources))]
public class AllowChineseCharacterNamesPlugIn : UpdatePlugInBase
{
    /// <inheritdoc/>
    public override string Name => Properties.CharacterNameUpdateResources.Name;

    /// <inheritdoc/>
    public override string Description => Properties.CharacterNameUpdateResources.Description;

    /// <inheritdoc/>
    public override string DataInitializationKey => DataInitialization.Id;

    /// <inheritdoc/>
    public override bool IsMandatory => false;

    /// <inheritdoc/>
    public override DateTime CreatedAt => new(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc/>
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        if (gameConfiguration.CharacterNameRegex == "^[a-zA-Z0-9]{3,10}$")
        {
            gameConfiguration.CharacterNameRegex = GameConfigurationInitializer.DefaultCharacterNameRegex;
        }

        return ValueTask.CompletedTask;
    }
}
