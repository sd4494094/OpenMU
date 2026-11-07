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
    /// Verifies that a fresh Season 6 configuration contains the stat reset NPC and its placement.
    /// </summary>
    [Test]
    public async Task FreshConfigurationContainsNpcAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        var config = await CreateConfigurationAsync(provider).ConfigureAwait(false);
        AssertPlacement(config);
    }

    /// <summary>
    /// Verifies that an upgrade repairs missing data and can be repeated without duplicate NPCs or spawns.
    /// </summary>
    [Test]
    public async Task UpgradeRepairsMissingNpcIdempotentlyAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        var config = await CreateConfigurationAsync(provider).ConfigureAwait(false);
        var npc = config.Monsters.Single(monster => monster.Number == 464);
        var map = config.Maps.Single(map => map.Number == 0);
        foreach (var spawn in map.MonsterSpawns.Where(spawn => spawn.MonsterDefinition == npc).ToArray())
        {
            map.MonsterSpawns.Remove(spawn);
        }

        config.Monsters.Remove(npc);
        using var context = provider.CreateNewContext();
        var update = new AddStatResetNpcPlugIn();
        await update.ApplyUpdateAsync(context, config).ConfigureAwait(false);
        await update.ApplyUpdateAsync(context, config).ConfigureAwait(false);
        AssertPlacement(config);
    }

    /// <summary>
    /// Verifies that an operator's NPC and placement customization is preserved.
    /// </summary>
    [Test]
    public async Task UpgradePreservesCustomizationAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        var config = await CreateConfigurationAsync(provider).ConfigureAwait(false);
        var npc = config.Monsters.Single(monster => monster.Number == 464);
        npc.NpcWindow = NpcWindow.Lahap;
        var spawn = config.Maps.Single(map => map.Number == 0).MonsterSpawns.Single(spawn => spawn.MonsterDefinition == npc);
        spawn.X1 = spawn.X2 = 140;
        using var context = provider.CreateNewContext();
        await new AddStatResetNpcPlugIn().ApplyUpdateAsync(context, config).ConfigureAwait(false);
        Assert.That(npc.NpcWindow, Is.EqualTo(NpcWindow.Lahap));
        Assert.That(spawn.X1, Is.EqualTo(140));
        Assert.That(config.Maps.Single(map => map.Number == 0).MonsterSpawns.Count(area => area.MonsterDefinition == npc), Is.EqualTo(1));
    }

    private static void AssertPlacement(GameConfiguration config)
    {
        var npc = config.Monsters.Single(monster => monster.Number == 464);
        Assert.That(npc.NpcWindow, Is.EqualTo(NpcWindow.StatReInitializer));
        Assert.That(npc.IsActive, Is.True);
        // A raw terrain value of 1 is a walkable safe-zone tile.
        Assert.That(config.Maps.Single(map => map.Number == 0).TerrainData![3 + (126 * 256) + 128], Is.EqualTo(1));
        var spawn = config.Maps.Single(map => map.Number == 0).MonsterSpawns.Single(spawn => spawn.MonsterDefinition == npc);
        Assert.That((spawn.X1, spawn.Y1, spawn.X2, spawn.Y2, spawn.Quantity), Is.EqualTo((128, 126, 128, 126, 1)));
    }

    private static async Task<GameConfiguration> CreateConfigurationAsync(InMemoryPersistenceContextProvider provider)
    {
        var initialization = new VersionSeasonSix.DataInitialization(provider, new NullLoggerFactory());
        await initialization.CreateInitialDataAsync(1, true).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        return (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).First();
    }
}
