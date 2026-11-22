// <copyright file="SetMuExWarpRequirementsUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Applies MuEx menu travel requirements and landing areas.
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
    public override int Version => 2;

    /// <inheritdoc />
    public override DateTime UpdatedAt => new(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    public override bool RunsOnNewDatabase => true;

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        var arena = GetRoute(gameConfiguration, 1, 6);
        var tarkan = GetRoute(gameConfiguration, 21, 8);
        var tarkan2 = GetRoute(gameConfiguration, 22, 8);
        var crywolf = GetRoute(gameConfiguration, 26, 34);
        var market = GetRoute(gameConfiguration, 44, 79);

        // Landing rectangles from MuEx gate.bmd entries 50, 57 and 77.
        SetMenuLanding(context, arena, 72, 140, 73, 181);
        SetMenuLanding(context, tarkan, 189, 63, 205, 72);
        SetMenuLanding(context, tarkan2, 96, 143, 100, 146);

        // Minimum level and fee from the corresponding entries in MuEx's movereq.bmd.
        crywolf.LevelRequirement = 10;
        crywolf.Costs = 0;
        market.LevelRequirement = 1;
        market.Costs = 10_000;
        return ValueTask.CompletedTask;
    }

    private static void SetMenuLanding(IContext context, WarpInfo route, byte x1, byte y1, byte x2, byte y2)
    {
        var existing = route.Gate!;
        if (!existing.IsSpawnGate && (existing.X1, existing.Y1, existing.X2, existing.Y2) == (x1, y1, x2, y2))
        {
            return;
        }

        // Spawn selection and physical entrances may still refer to the original gate.
        var target = context.CreateNew<ExitGate>();
        var map = existing.Map!;
        target.Map = map;
        target.Direction = existing.Direction;
        target.X1 = x1;
        target.Y1 = y1;
        target.X2 = x2;
        target.Y2 = y2;
        map.ExitGates.Add(target);
        route.Gate = target;
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
