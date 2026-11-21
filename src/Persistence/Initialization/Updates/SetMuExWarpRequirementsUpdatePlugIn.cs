// <copyright file="SetMuExWarpRequirementsUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Applies the MuEx menu requirements for traveling to Crywolf Fortress and Loren Market.
/// </summary>
[PlugIn]
[Display(Name = nameof(PlugInResources.SetMuExWarpRequirements_Name), Description = nameof(PlugInResources.SetMuExWarpRequirements_Description), ResourceType = typeof(PlugInResources))]
[Guid("28b930d3-db4f-4707-b491-9bb3debfc7fb")]
public sealed class SetMuExWarpRequirementsUpdatePlugIn : UpdatePlugInBase
{
    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override string Name => PlugInResources.SetMuExWarpRequirements_Name;

    /// <inheritdoc />
    public override string Description => PlugInResources.SetMuExWarpRequirements_Description;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    public override bool RunsOnNewDatabase => true;

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        var crywolf = GetRoute(gameConfiguration, 26, 34);
        var market = GetRoute(gameConfiguration, 44, 79);

        // Minimum level and fee from the corresponding entries in MuEx's movereq.bmd.
        crywolf.LevelRequirement = 10;
        crywolf.Costs = 0;
        market.LevelRequirement = 1;
        market.Costs = 10_000;
        return ValueTask.CompletedTask;
    }

    private static WarpInfo GetRoute(GameConfiguration configuration, int index, int map)
    {
        var route = configuration.WarpList.SingleOrDefault(route => route.Index == index);
        if (route?.Gate?.Map?.Number != map)
        {
            throw new InvalidOperationException($"Expected warp {index} to lead to map {map}.");
        }

        return route;
    }
}
