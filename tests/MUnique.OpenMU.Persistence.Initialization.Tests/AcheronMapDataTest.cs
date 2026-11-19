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
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Any(log => log.Key == update.Key), Is.False);

        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        var map = configuration.Maps.Single(map => map.Number == 91);
        this.AssertMapData(map);

        var firstGate = map.EnterGates.Single(gate => gate.Number == 418);
        firstGate.LevelRequirement = 120;
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(configuration.Maps.Single(map => map.Number == 91), Is.SameAs(map));
        Assert.That(firstGate.LevelRequirement, Is.EqualTo(120));
        Assert.That(map.EnterGates, Has.Count.EqualTo(4));
        Assert.That(map.ExitGates, Has.Count.EqualTo(4));
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
        Assert.That(map.ExitGates, Has.Count.EqualTo(4));
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
