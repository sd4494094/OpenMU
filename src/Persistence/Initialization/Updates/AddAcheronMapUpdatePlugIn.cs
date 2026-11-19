// <copyright file="AddAcheronMapUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence.Initialization.Properties;
using MUnique.OpenMU.Persistence.Initialization.Version075.Maps;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Adds the Acheron terrain, internal gates and menu warp to the game configuration.
/// </summary>
[PlugIn]
[Display(Name = UpdateName, Description = UpdateDescription)]
[Guid("841714BB-D48A-4B79-AA36-083310BDD6ED")]
public sealed class AddAcheronMapUpdatePlugIn : UpdatePlugInBase
{
    private const byte MapNumber = 91;
    private const int WarpIndex = 40;
    private const string UpdateName = "Acheron map 91";
    private const string UpdateDescription = "Adds the Acheron terrain, internal gates and menu warp for the MuEx client.";
    private static readonly (short Number, GateRectangle Entrance, GateRectangle Exit)[] InternalGates =
    [
        (418, new(26, 168, 28, 172), new(112, 161, 114, 166)),
        (420, new(115, 168, 118, 170), new(33, 167, 37, 170)),
        (422, new(82, 204, 86, 205), new(151, 198, 156, 200)),
        (424, new(147, 202, 148, 206), new(81, 197, 83, 199)),
    ];

    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override string Name => UpdateName;

    /// <inheritdoc />
    public override string Description => UpdateDescription;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override int Version => 2;

    /// <inheritdoc />
    public override DateTime UpdatedAt => new(2026, 11, 19, 21, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    public override bool RunsOnNewDatabase => true;

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        var map = gameConfiguration.Maps.FirstOrDefault(map => map.Number == MapNumber)
                  ?? CreateMap(context, gameConfiguration);
        if (gameConfiguration.WarpList.Any(warp => warp.Index == WarpIndex))
        {
            return ValueTask.CompletedTask;
        }

        // The MuEx move list entry 40 targets gate 453, a non-spawn exit in Acheron.
        var target = context.CreateNew<ExitGate>();
        target.Map = map;
        new GateRectangle(62, 213, 64, 214).SetBounds(target);
        target.Direction = Direction.South;
        map.ExitGates.Add(target);

        var warp = context.CreateNew<WarpInfo>();
        warp.Index = WarpIndex;
        warp.Name = map.Name;
        warp.Costs = 2_000;
        warp.LevelRequirement = 100;
        warp.Gate = target;
        gameConfiguration.WarpList.Add(warp);
        return ValueTask.CompletedTask;
    }

    private static GameMapDefinition CreateMap(IContext context, GameConfiguration gameConfiguration)
    {
        var terrain = TerrainUpdateHelper.ReadTerrainResource("Terrain92.att")
            ?? throw new InvalidOperationException("The Acheron server terrain resource is missing.");
        var safezoneMap = gameConfiguration.Maps.Single(map => map.Number == Lorencia.Number);
        var map = context.CreateNew<GameMapDefinition>();
        map.SetGuid(MapNumber, 0);
        map.Number = MapNumber;
        map.Name = MapNames.ResourceManager.GetLocalizedString("Acheron");
        map.TerrainData = terrain;
        map.ExpMultiplier = 1;
        map.SafezoneMap = safezoneMap;
        foreach (var (number, entrance, exit) in InternalGates)
        {
            var target = context.CreateNew<ExitGate>();
            target.Map = map;
            exit.SetBounds(target);
            map.ExitGates.Add(target);

            var gate = context.CreateNew<EnterGate>();
            gate.Number = number;
            gate.TargetGate = target;
            entrance.SetBounds(gate);
            map.EnterGates.Add(gate);
        }

        gameConfiguration.Maps.Add(map);
        return map;
    }

    private readonly record struct GateRectangle(byte X1, byte Y1, byte X2, byte Y2)
    {
        public void SetBounds(Gate gate)
        {
            gate.X1 = this.X1;
            gate.Y1 = this.Y1;
            gate.X2 = this.X2;
            gate.Y2 = this.Y2;
        }
    }
}
