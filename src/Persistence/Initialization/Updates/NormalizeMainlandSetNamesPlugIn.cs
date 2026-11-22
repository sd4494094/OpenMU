// <copyright file="NormalizeMainlandSetNamesPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Persistence.Initialization.Captions;
using MUnique.OpenMU.Persistence.Initialization.Properties;
using MUnique.OpenMU.PlugIns;

/// <summary>Aligns built-in Chinese ancient set captions with the mainland owner spelling.</summary>
[PlugIn]
[Guid("6918af8a-a604-48d8-8e3f-94a382ce7af3")]
[Display(Name = nameof(MainlandSetNameUpdateResources.Name), Description = nameof(MainlandSetNameUpdateResources.Description), ResourceType = typeof(MainlandSetNameUpdateResources))]
public class NormalizeMainlandSetNamesPlugIn : UpdatePlugInBase
{
    private static readonly HashSet<string> Sources = new[]
    {
        nameof(ItemSetNames.Anubis), nameof(ItemSetNames.Enis),
    }.SelectMany(key => new[] { $"ItemSetNames/{key}", $"ItemOptionNames/{key}AncientSet" }).ToHashSet(StringComparer.Ordinal);

    /// <inheritdoc/>
    public override string Name => MainlandSetNameUpdateResources.Name;

    /// <inheritdoc/>
    public override string Description => MainlandSetNameUpdateResources.Description;

    /// <inheritdoc/>
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc/>
    public override bool IsMandatory => false;

    /// <inheritdoc/>
    public override DateTime CreatedAt => new(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc/>
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        ConfigurationNameSources.Register();
        var changes = ConfigurationCaptions.DetermineChanges(gameConfiguration)
            .Where(change => Sources.Contains(change.SourceKey) && change.CultureName == "zh-CN" && change.IsRecommended && change.SourceText is { Length: > 0 });
        ConfigurationCaptions.ApplyChanges(gameConfiguration, changes.Select(change => change.Id));
        return ValueTask.CompletedTask;
    }
}
