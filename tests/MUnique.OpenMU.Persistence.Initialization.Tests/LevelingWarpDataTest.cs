// <copyright file="LevelingWarpDataTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.LevelingWarp;
using MUnique.OpenMU.Persistence.Initialization.Updates;
using MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix;
using MUnique.OpenMU.Persistence.InMemory;

/// <summary>
/// Verifies fresh and existing configuration data without changing ordinary M destinations.
/// </summary>
[TestFixture]
public class LevelingWarpDataTest
{
    /// <summary>Validates every landing and optional feature isolation, including idempotent upgrade.</summary>
    [Test]
    public async Task FreshAndUpgradeKeepPublicWarpsUnchangedAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new DataInitialization(provider, NullLoggerFactory.Instance).CreateInitialDataAsync(1, false).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var warps = configuration.WarpList.ToArray();
        var gateCount = configuration.Maps.Sum(m => m.ExitGates.Count);
        var feature = configuration.PlugInConfigurations.Single(p => p.TypeId == typeof(LevelingWarpFeaturePlugIn).GUID);
        var talk = configuration.PlugInConfigurations.Single(p => p.TypeId == typeof(LevelingWarpNpcPlugIn).GUID);
        var talkJson = talk.CustomConfiguration;
        Assert.That(talkJson, Does.Not.Contain("\"Npc\": null"));
        Assert.That(talkJson, Does.Contain("$ref"));
        var json = feature.CustomConfiguration;
        Assert.That(json, Does.Contain("Ghost Room"));
        Assert.That(feature.IsActive, Is.False);
        await new AddLevelingWarpNpcPlugIn().ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        await new AddLevelingWarpNpcPlugIn().ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(configuration.WarpList, Is.EqualTo(warps));
        Assert.That(configuration.Maps.Sum(m => m.ExitGates.Count), Is.EqualTo(gateCount));
        Assert.That(feature.CustomConfiguration, Is.EqualTo(json));
        Assert.That(talk.CustomConfiguration, Is.EqualTo(talkJson));
        Assert.That(configuration.Monsters.Count(n => n.Number == LevelingWarpInitialization.NpcNumber), Is.EqualTo(1));
        var npc = configuration.Monsters.Single(n => n.Number == LevelingWarpInitialization.NpcNumber);
        var spawn = configuration.Maps.Single(m => m.Number == 0).MonsterSpawns.Single(s => s.MonsterDefinition == npc);
        Assert.That((spawn.X1, spawn.Y1), Is.EqualTo((LevelingWarpInitialization.NpcX, LevelingWarpInitialization.NpcY)));
        var data = LevelingWarpInitialization.CreateDestinations(context, configuration);
        Assert.That(data.Destinations.Count, Is.EqualTo(26));
        Assert.That(data.Destinations.Select(d => d.Number).Distinct().Count(), Is.EqualTo(26));
        foreach (var entry in data.Destinations)
        {
            var gate = entry.Gate!;
            Assert.That(gate.IsSpawnGate, Is.False);
            Assert.That(new GameMapTerrain(gate.Map!).WalkMap[gate.X1, gate.Y1], Is.True, entry.Name.ToString());
            Assert.That(gate.X1, Is.EqualTo(gate.X2));
            Assert.That(gate.Y1, Is.EqualTo(gate.Y2));
            Assert.That(entry.Costs, Is.GreaterThanOrEqualTo(0));
        }
    }
    /// <summary>An existing database without this optional service receives it without changing public warps.</summary>
    [Test]
    public async Task ExistingConfigurationReceivesMissingOptionalServiceAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new DataInitialization(provider, NullLoggerFactory.Instance).CreateInitialDataAsync(1, false).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var config = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var warps = config.WarpList.ToArray();
        foreach (var entry in config.PlugInConfigurations.Where(p => p.TypeId == typeof(LevelingWarpFeaturePlugIn).GUID || p.TypeId == typeof(LevelingWarpNpcPlugIn).GUID).ToArray())
        {
            config.PlugInConfigurations.Remove(entry);
        }

        var npc = config.Monsters.Single(n => n.Number == 710);
        config.Monsters.Remove(npc);
        var town = config.Maps.Single(m => m.Number == 0);
        foreach (var spawn in town.MonsterSpawns.Where(s => s.MonsterDefinition == npc).ToArray())
        {
            town.MonsterSpawns.Remove(spawn);
        }

        await new AddLevelingWarpNpcPlugIn().ApplyUpdateAsync(context, config).ConfigureAwait(false);
        Assert.That(config.Monsters.Count(n => n.Number == 710), Is.EqualTo(1));
        Assert.That(config.PlugInConfigurations.Single(p => p.TypeId == typeof(LevelingWarpFeaturePlugIn).GUID).IsActive, Is.False);
        Assert.That(config.WarpList, Is.EqualTo(warps));
    }

}
