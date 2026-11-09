// <copyright file="AddCreatorGoddessNpcPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Adds the decorative creator goddess next to the existing leveling teleport guide.
/// </summary>
[PlugIn]
[Guid("60161d7c-4c44-4b3f-9607-daac43ab7eb0")]
[Display(Name = nameof(Properties.PlugInResources.AddCreatorGoddessNpc_Name), Description = nameof(Properties.PlugInResources.AddCreatorGoddessNpc_Description), ResourceType = typeof(Properties.PlugInResources))]
public class AddCreatorGoddessNpcPlugIn : UpdatePlugInBase
{
    /// <inheritdoc/>
    public override string Name => Properties.PlugInResources.AddCreatorGoddessNpc_Name;

    /// <inheritdoc/>
    public override string Description => Properties.PlugInResources.AddCreatorGoddessNpc_Description;

    /// <inheritdoc/>
    public override string DataInitializationKey => DataInitialization.Id;

    /// <inheritdoc/>
    public override bool IsMandatory => false;

    /// <inheritdoc/>
    public override DateTime CreatedAt => new(2026, 11, 9, 15, 30, 0, DateTimeKind.Utc);

    /// <inheritdoc/>
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        CreatorGoddessInitialization.EnsureConfiguration(context, gameConfiguration);
        return ValueTask.CompletedTask;
    }
}
