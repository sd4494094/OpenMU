// <copyright file="CreatorGoddessDataTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.Persistence.Initialization.Updates;
using MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix;
using MUnique.OpenMU.Persistence.InMemory;

/// <summary>
/// Verifies the goddess's new database initialization and non-destructive existing database upgrade.
/// </summary>
[TestFixture]
internal class CreatorGoddessDataTest
{
    /// <summary>Checks the real initialized terrain and existing NPC placements.</summary>
    [Test]
    public async Task FreshConfigurationPlacesGoddessOnSafeStreetAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new DataInitialization(provider, new NullLoggerFactory()).CreateInitialDataAsync(1, true).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var config = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        AssertConfiguration(config);
        var town = config.Maps.Single(map => map.Number == 0);
        for (var y = CreatorGoddessInitialization.NpcY - 1; y <= CreatorGoddessInitialization.NpcY + 1; y++)
        {
            for (var x = CreatorGoddessInitialization.NpcX - 1; x <= CreatorGoddessInitialization.NpcX + 1; x++)
            {
                Assert.That(town.TerrainData![3 + (y * 256) + x], Is.EqualTo(1), $"Safe walkable tile {x},{y}");
            }
        }

        Assert.That(town.MonsterSpawns.Count(spawn => spawn.X1 <= CreatorGoddessInitialization.NpcX
            && spawn.X2 >= CreatorGoddessInitialization.NpcX && spawn.Y1 <= CreatorGoddessInitialization.NpcY
            && spawn.Y2 >= CreatorGoddessInitialization.NpcY), Is.EqualTo(1));
        Assert.That(config.Monsters.Single(monster => monster.Number == CreatorGoddessNpcPlugIn.NpcNumber)
            .Designation.GetTranslation(CultureInfo.GetCultureInfo("en")), Is.EqualTo("创世神·艾莉雅"));
    }

    /// <summary>Repairs a missing NPC and repeats safely without duplicating any records.</summary>
    [Test]
    public async Task ExistingConfigurationUpgradeIsIdempotentAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        using var context = provider.CreateNewContext();
        var config = context.CreateNew<GameConfiguration>();
        var town = context.CreateNew<GameMapDefinition>();
        town.Number = 0;
        config.Maps.Add(town);
        var update = new AddCreatorGoddessNpcPlugIn();
        await update.ApplyUpdateAsync(context, config).ConfigureAwait(false);
        var npc = config.Monsters.Single();
        var spawn = town.MonsterSpawns.Single();
        var talk = config.PlugInConfigurations.Single();
        await update.ApplyUpdateAsync(context, config).ConfigureAwait(false);
        AssertConfiguration(config);
        Assert.That(config.Monsters.Single(), Is.SameAs(npc));
        Assert.That(town.MonsterSpawns.Single(), Is.SameAs(spawn));
        Assert.That(config.PlugInConfigurations.Single(), Is.SameAs(talk));
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Count(entry => entry.Key == update.Key), Is.EqualTo(1));
        Assert.That(update.IsMandatory, Is.False);
    }

    /// <summary>Preserves operator naming, disabled services, non-default coordinates and spawn quantities.</summary>
    [Test]
    public async Task UpgradePreservesOwnerCustomizationsAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        using var context = provider.CreateNewContext();
        var config = context.CreateNew<GameConfiguration>();
        var town = context.CreateNew<GameMapDefinition>();
        town.Number = 0;
        config.Maps.Add(town);
        CreatorGoddessInitialization.EnsureConfiguration(context, config);
        var npc = config.Monsters.Single();
        var spawn = town.MonsterSpawns.Single();
        var talk = config.PlugInConfigurations.Single();
        npc.Designation = "Owner name";
        spawn.X1 = spawn.X2 = 151;
        spawn.Quantity = 0;
        talk.IsActive = false;
        var customConfiguration = talk.CustomConfiguration;
        await new AddCreatorGoddessNpcPlugIn().ApplyUpdateAsync(context, config).ConfigureAwait(false);
        Assert.That(npc.Designation.ToString(), Is.EqualTo("Owner name"));
        Assert.That(spawn.X1, Is.EqualTo(151));
        Assert.That(spawn.Quantity, Is.Zero);
        Assert.That(talk.IsActive, Is.False);
        Assert.That(talk.CustomConfiguration, Is.EqualTo(customConfiguration));
        Assert.That(config.Monsters.Count, Is.EqualTo(1));
        Assert.That(town.MonsterSpawns.Count, Is.EqualTo(1));
    }

    /// <summary>Does not create an extra Lorencia instance when the operator moved the goddess to another map.</summary>
    [Test]
    public async Task UpgradePreservesPlacementOnAnotherMapAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        using var context = provider.CreateNewContext();
        var config = context.CreateNew<GameConfiguration>();
        var town = context.CreateNew<GameMapDefinition>();
        town.Number = 0;
        config.Maps.Add(town);
        CreatorGoddessInitialization.EnsureConfiguration(context, config);
        var other = context.CreateNew<GameMapDefinition>();
        other.Number = 3;
        config.Maps.Add(other);
        var spawn = town.MonsterSpawns.Single();
        town.MonsterSpawns.Remove(spawn);
        other.MonsterSpawns.Add(spawn);
        await new AddCreatorGoddessNpcPlugIn().ApplyUpdateAsync(context, config).ConfigureAwait(false);
        Assert.That(town.MonsterSpawns, Is.Empty);
        Assert.That(other.MonsterSpawns.Single(), Is.SameAs(spawn));
    }

    private static void AssertConfiguration(GameConfiguration config)
    {
        var npc = config.Monsters.Single(monster => monster.Number == CreatorGoddessNpcPlugIn.NpcNumber);
        Assert.That(npc.ObjectKind, Is.EqualTo(NpcObjectKind.PassiveNpc));
        Assert.That(npc.NpcWindow, Is.EqualTo(NpcWindow.Undefined));
        var spawn = config.Maps.Single(map => map.Number == 0).MonsterSpawns.Single(area => area.MonsterDefinition == npc);
        Assert.That((spawn.X1, spawn.Y1, spawn.X2, spawn.Y2, spawn.Quantity),
            Is.EqualTo((CreatorGoddessInitialization.NpcX, CreatorGoddessInitialization.NpcY, CreatorGoddessInitialization.NpcX, CreatorGoddessInitialization.NpcY, 1)));
        Assert.That(spawn.SpawnTrigger, Is.EqualTo(SpawnTrigger.Automatic));
        Assert.That(config.PlugInConfigurations.Single(plugin => plugin.TypeId == typeof(CreatorGoddessNpcPlugIn).GUID).IsActive, Is.True);
    }
}
