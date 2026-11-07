// <copyright file="StatResetNpcDataTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Persistence.Initialization.Updates;
using MUnique.OpenMU.Persistence.InMemory;

/// <summary>
/// Tests fresh initialization and existing configuration upgrades for the stat reset NPC.
/// </summary>
[TestFixture]
internal class StatResetNpcDataTest
{
    /// <summary>
    /// Verifies the outdoor location, nearby safe tiles and absence of overlapping NPCs in fresh data.
    /// </summary>
    [Test]
    public async Task FreshConfigurationContainsOutdoorNpcAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        var initialization = new VersionSeasonSix.DataInitialization(provider, new NullLoggerFactory());
        await initialization.CreateInitialDataAsync(1, true).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var config = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).First();
        AssertPlacement(config);
        var map = config.Maps.Single(map => map.Number == 0);
        for (var y = 127; y <= 129; y++)
        {
            for (var x = 144; x <= 146; x++)
            {
                Assert.That(map.TerrainData![3 + (y * 256) + x], Is.EqualTo(1), $"Tile ({x}, {y}) must be walkable and safe.");
            }
        }

        Assert.That(map.MonsterSpawns.Any(area => area.MonsterDefinition is { Number: not 464, ObjectKind: NpcObjectKind.PassiveNpc }
            && area.X1 <= 145 && area.X2 >= 145 && area.Y1 <= 128 && area.Y2 >= 128), Is.False);
    }

    /// <summary>
    /// Repairs missing data cumulatively and avoids duplicate NPCs or spawns when repeated.
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    public async Task UpgradeRepairsMissingDataIdempotentlyAsync(bool removeNpc)
    {
        var provider = new InMemoryPersistenceContextProvider();
        var config = CreateExistingConfiguration(provider);
        var npc = config.Monsters.Single();
        config.Maps.Single().MonsterSpawns.Clear();
        if (removeNpc)
        {
            config.Monsters.Remove(npc);
        }

        using var context = provider.CreateNewContext();
        var update = new AddStatResetNpcPlugIn();
        await update.ApplyUpdateAsync(context, config).ConfigureAwait(false);
        await update.ApplyUpdateAsync(context, config).ConfigureAwait(false);
        AssertPlacement(config);
        Assert.That(config.Monsters.Count(monster => monster.Number == 464), Is.EqualTo(1));
    }

    /// <summary>
    /// Migrates the original location in place, both for version 1 and for unversioned installations.
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    public async Task UpgradeMovesOriginalPlacementInPlaceAsync(bool versionOneInstalled)
    {
        var provider = new InMemoryPersistenceContextProvider();
        var config = CreateExistingConfiguration(provider);
        var original = config.Maps.Single().MonsterSpawns.Single();
        using var context = provider.CreateNewContext();
        var update = new AddStatResetNpcPlugIn();
        if (versionOneInstalled)
        {
            var entry = context.CreateNew<ConfigurationUpdate>();
            entry.Key = update.Key;
            entry.Version = 1;
        }

        await update.ApplyUpdateAsync(context, config).ConfigureAwait(false);
        await update.ApplyUpdateAsync(context, config).ConfigureAwait(false);
        AssertPlacement(config);
        Assert.That(config.Maps.Single().MonsterSpawns.Single(), Is.SameAs(original));
        var installed = (await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Single(entry => entry.Key == update.Key);
        Assert.That(installed.Version, Is.EqualTo(2));
        Assert.That(update.IsMandatory, Is.False);
        Assert.That(update.Key, Is.EqualTo(new Guid("108ae690-0a2e-444b-a36a-938ee8268aaa")));
    }

    /// <summary>
    /// Preserves operator customizations instead of treating them as the old default.
    /// </summary>
    [TestCase("window")]
    [TestCase("position")]
    [TestCase("quantity")]
    [TestCase("direction")]
    [TestCase("trigger")]
    [TestCase("additional-spawn")]
    public async Task UpgradePreservesCustomizationAsync(string customization)
    {
        var provider = new InMemoryPersistenceContextProvider();
        var config = CreateExistingConfiguration(provider);
        var npc = config.Monsters.Single();
        var map = config.Maps.Single();
        var spawn = map.MonsterSpawns.Single();
        using var context = provider.CreateNewContext();
        switch (customization)
        {
            case "window":
                npc.NpcWindow = NpcWindow.Lahap;
                break;
            case "position":
                spawn.X1 = spawn.X2 = 140;
                break;
            case "quantity":
                spawn.Quantity = 2;
                break;
            case "direction":
                spawn.Direction = Direction.North;
                break;
            case "trigger":
                spawn.SpawnTrigger = SpawnTrigger.Wandering;
                break;
            case "additional-spawn":
                var additional = context.CreateNew<MonsterSpawnArea>();
                additional.MonsterDefinition = npc;
                additional.X1 = additional.X2 = 150;
                additional.Y1 = additional.Y2 = 130;
                map.MonsterSpawns.Add(additional);
                break;
        }

        var window = npc.NpcWindow;
        var placements = map.MonsterSpawns.Select(area => (area.X1, area.Y1, area.X2, area.Y2, area.Quantity, area.Direction, area.SpawnTrigger)).ToArray();
        var update = new AddStatResetNpcPlugIn();
        await update.ApplyUpdateAsync(context, config).ConfigureAwait(false);
        await update.ApplyUpdateAsync(context, config).ConfigureAwait(false);
        Assert.That(npc.NpcWindow, Is.EqualTo(window));
        Assert.That(map.MonsterSpawns.Select(area => (area.X1, area.Y1, area.X2, area.Y2, area.Quantity, area.Direction, area.SpawnTrigger)), Is.EqualTo(placements));
    }

    private static void AssertPlacement(GameConfiguration config)
    {
        var npc = config.Monsters.Single(monster => monster.Number == 464);
        Assert.That(npc.NpcWindow, Is.EqualTo(NpcWindow.StatReInitializer));
        Assert.That(npc.IsActive, Is.True);
        var spawn = config.Maps.Single(map => map.Number == 0).MonsterSpawns.Single(spawn => spawn.MonsterDefinition == npc);
        Assert.That((spawn.X1, spawn.Y1, spawn.X2, spawn.Y2, spawn.Quantity), Is.EqualTo((145, 128, 145, 128, 1)));
    }

    private static GameConfiguration CreateExistingConfiguration(InMemoryPersistenceContextProvider provider)
    {
        using var context = provider.CreateNewContext();
        var config = context.CreateNew<GameConfiguration>();
        var map = context.CreateNew<GameMapDefinition>();
        map.Number = 0;
        config.Maps.Add(map);
        var npc = context.CreateNew<MonsterDefinition>();
        npc.Number = 464;
        npc.ObjectKind = NpcObjectKind.PassiveNpc;
        npc.NpcWindow = NpcWindow.StatReInitializer;
        npc.IsActive = true;
        config.Monsters.Add(npc);
        var spawn = context.CreateNew<MonsterSpawnArea>();
        spawn.MonsterDefinition = npc;
        spawn.X1 = spawn.X2 = 128;
        spawn.Y1 = spawn.Y2 = 126;
        spawn.Quantity = 1;
        spawn.Direction = Direction.SouthEast;
        spawn.SpawnTrigger = SpawnTrigger.Automatic;
        map.MonsterSpawns.Add(spawn);
        return config;
    }
}
