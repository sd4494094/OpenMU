// <copyright file="AcheronMapDataTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.Persistence.Initialization.Updates;

/// <summary>
/// Checks the optional Acheron map data update.
/// </summary>
[TestFixture]
internal class AcheronMapDataTest
{
    /// <summary>
    /// Initializes a fresh database, applies the update and replays it with a customized map.
    /// </summary>
    [Test]
    public async Task ImportsMapAndPreservesExistingConfigurationAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        var initialize = new VersionSeasonSix.DataInitialization(provider, new NullLoggerFactory());
        await initialize.CreateInitialDataAsync(1, true).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var update = new AddAcheronMapUpdatePlugIn();
        Assert.That(configuration.Maps.Any(map => map.Number == 91), Is.False);
        Assert.That(configuration.WarpList.Any(warp => warp.Index == 40), Is.False);
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Any(log => log.Key == update.Key), Is.False);

        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        var map = configuration.Maps.Single(map => map.Number == 91);
        this.AssertMapData(map);
        var warp = configuration.WarpList.Single(candidate => candidate.Index == 40);
        AssertWarp(warp, map);

        var firstGate = map.EnterGates.Single(gate => gate.Number == 418);
        firstGate.LevelRequirement = 120;
        warp.Costs = 4_000;
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(configuration.Maps.Single(map => map.Number == 91), Is.SameAs(map));
        Assert.That(firstGate.LevelRequirement, Is.EqualTo(120));
        Assert.That(map.EnterGates, Has.Count.EqualTo(4));
        Assert.That(map.ExitGates, Has.Count.EqualTo(5));
        Assert.That(configuration.WarpList.Single(candidate => candidate.Index == 40), Is.SameAs(warp));
        Assert.That(warp.Costs, Is.EqualTo(4_000));
    }

    /// <summary>
    /// Upgrades an installed version-one Acheron map and keeps its internal gates.
    /// </summary>
    [Test]
    public async Task AddsMenuWarpToVersionOneAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        var initialize = new VersionSeasonSix.DataInitialization(provider, new NullLoggerFactory());
        await initialize.CreateInitialDataAsync(1, true).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var update = new AddAcheronMapUpdatePlugIn();
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        var map = configuration.Maps.Single(candidate => candidate.Number == 91);
        var oldGate = map.EnterGates.First();
        var warp = configuration.WarpList.Single(candidate => candidate.Index == 40);
        configuration.WarpList.Remove(warp);
        map.ExitGates.Remove(warp.Gate!);
        var installed = (await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Single(entry => entry.Key == update.Key);
        installed.Version = 1;

        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);

        Assert.That(installed.Version, Is.EqualTo(2));
        Assert.That(configuration.Maps.Single(candidate => candidate.Number == 91), Is.SameAs(map));
        Assert.That(map.EnterGates, Does.Contain(oldGate));
        Assert.That(map.ExitGates, Has.Count.EqualTo(5));
        AssertWarp(configuration.WarpList.Single(candidate => candidate.Index == 40), map);
    }

    private static void AssertWarp(WarpInfo warp, GameMapDefinition map)
    {
        var gate = warp.Gate!;
        Assert.That(gate.Map, Is.SameAs(map));
        Assert.That(gate.IsSpawnGate, Is.False);
        Assert.That(gate.Direction, Is.EqualTo(Direction.South));
        Assert.That(warp.Name.Value, Is.EqualTo(map.Name.Value));
        Assert.That(warp.LevelRequirement, Is.EqualTo(100));
        Assert.That(warp.Costs, Is.EqualTo(2_000));
        Assert.That(new[] { gate.X1, gate.Y1, gate.X2, gate.Y2 }, Is.EqualTo(new byte[] { 62, 213, 64, 214 }));
        var terrain = new GameMapTerrain(map);
        for (int x = gate.X1; x <= gate.X2; ++x)
        {
            for (int y = gate.Y1; y <= gate.Y2; ++y)
            {
                Assert.That(terrain.WalkMap[x, y] && terrain.SafezoneMap[x, y], Is.True);
            }
        }
    }

    private void AssertMapData(GameMapDefinition map)
    {
        var coordinates = new[]
        {
            new byte[] { 26, 168, 28, 172, 112, 161, 114, 166 },
            new byte[] { 115, 168, 118, 170, 33, 167, 37, 170 },
            new byte[] { 82, 204, 86, 205, 151, 198, 156, 200 },
            new byte[] { 147, 202, 148, 206, 81, 197, 83, 199 },
        };
        var gates = map.EnterGates.OrderBy(gate => gate.Number).ToList();
        Assert.That(gates.Select(gate => gate.Number), Is.EqualTo(new short[] { 418, 420, 422, 424 }));
        Assert.That(map.ExitGates, Has.Count.EqualTo(5));
        Assert.That(map.ExitGates.Any(gate => gate.IsSpawnGate), Is.False);
        Assert.That(map.MonsterSpawns, Is.Empty);
        Assert.That(map.SafezoneMap?.Number, Is.EqualTo(0));
        Assert.That(Convert.ToHexStringLower(SHA256.HashData(map.TerrainData!)),
            Is.EqualTo("b2a5d470ac27d5765cf06cdab67f9076d2f00494512694ada66f5387f11064ab"));
        var terrain = new GameMapTerrain(map);
        foreach (var (gate, expected) in gates.Zip(coordinates))
        {
            var target = gate.TargetGate!;
            Assert.That(target.Map, Is.SameAs(map));
            Assert.That(new[] { gate.X1, gate.Y1, gate.X2, gate.Y2, target.X1, target.Y1, target.X2, target.Y2 },
                Is.EqualTo(expected));
            Assert.That(gate.LevelRequirement, Is.Zero);
            Assert.That(target.Direction, Is.EqualTo(Direction.Undefined));
            for (var y = (int)target.Y1; y <= target.Y2; y++)
            {
                for (var x = (int)target.X1; x <= target.X2; x++)
                {
                    Assert.That(terrain.WalkMap[x, y], Is.True);
                }
            }
        }
    }
}
