// <copyright file="LevelingWarpDataTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.LevelingWarp;
using MUnique.OpenMU.Persistence.Initialization.Updates;
using MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Verifies fresh and existing configuration data without changing ordinary M destinations.
/// </summary>
[TestFixture]
public class LevelingWarpDataTest
{
    /// <summary>Uses the same localized-string serialization as Startup.</summary>
    [SetUp]
    public void SetUp()
    {
        if (!JsonConverterRegistry.Converters.OfType<LocalizedStringJsonConverter>().Any())
        {
            JsonConverterRegistry.RegisterConverter(new LocalizedStringJsonConverter());
        }
    }

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
        Assert.That(data.Destinations.Count, Is.EqualTo(28));
        Assert.That(data.Destinations.Select(d => d.Number).Distinct().Count(), Is.EqualTo(28));
        foreach (var entry in data.Destinations)
        {
            var gate = entry.Gate!;
            Assert.That(gate.IsSpawnGate, Is.False);
            Assert.That(new GameMapTerrain(gate.Map!).WalkMap[gate.X1, gate.Y1], Is.True, entry.Name.ToString());
            Assert.That(gate.X1, Is.EqualTo(gate.X2));
            Assert.That(gate.Y1, Is.EqualTo(gate.Y2));
            Assert.That(entry.Costs, Is.GreaterThanOrEqualTo(0));
        }

        var yetiRooms = data.Destinations.Where(entry => entry.Number is 23 or 24).OrderBy(entry => entry.Number).ToArray();
        Assert.That(yetiRooms.Select(entry => (entry.Gate!.Map!.Number, entry.Gate.X1, entry.Gate.Y1)),
            Is.EqualTo(new[] { ((short)2, (byte)195, (byte)165), ((short)2, (byte)37, (byte)25) }));
        Assert.That(yetiRooms.Select(entry => entry.LevelRequirement), Is.All.EqualTo(20));
        Assert.That(yetiRooms.Select(entry => entry.Costs), Is.All.EqualTo(2000));
    }

    /// <summary>Upgrades an existing default list without replacing owner-edited values or adding duplicates.</summary>
    [Test]
    public async Task VersionThreeAddsOnlyMissingYetiDestinationsAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new DataInitialization(provider, NullLoggerFactory.Instance).CreateInitialDataAsync(1, false).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var feature = configuration.PlugInConfigurations.Single(p => p.TypeId == typeof(LevelingWarpFeaturePlugIn).GUID);
        var entries = JsonNode.Parse(feature.CustomConfiguration!)!;
        var destinations = entries["Destinations"]!["$values"]!.AsArray();
        foreach (var number in new[] { 23, 24 })
        {
            var entry = destinations.Single(node => node!["Number"]!.GetValue<int>() == number)!;
            var gateId = entry["Gate"]!["$ref"]!.GetValue<string>();
            destinations.Remove(entry);
            var gate = configuration.Maps.SelectMany(map => map.ExitGates).Single(candidate => ((MUnique.OpenMU.Persistence.IIdentifiable)candidate).Id.ToString() == gateId);
            gate.Map!.ExitGates.Remove(gate);
        }

        var existing = destinations.Single(entry => entry!["Number"]!.GetValue<int>() == 1)!;
        existing["Costs"] = 54321;
        feature.CustomConfiguration = entries.ToJsonString();
        feature.IsActive = true;
        var warpList = configuration.WarpList.ToArray();
        var gateCount = configuration.Maps.Sum(map => map.ExitGates.Count);
        var update = new AddLevelingWarpNpcPlugIn();
        var installed = (await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Single(item => item.Key == update.Key);
        installed.Version = 2;

        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);

        Assert.That(installed.Version, Is.EqualTo(3));
        Assert.That(feature.IsActive, Is.True);
        Assert.That(configuration.WarpList, Is.EqualTo(warpList));
        Assert.That(configuration.Maps.Sum(map => map.ExitGates.Count), Is.EqualTo(gateCount + 2));
        var upgraded = JsonNode.Parse(feature.CustomConfiguration!)!["Destinations"]!["$values"]!.AsArray();
        Assert.That(upgraded.Count, Is.EqualTo(28));
        Assert.That(upgraded.Single(entry => entry!["Number"]!.GetValue<int>() == 1)!["Costs"]!.GetValue<int>(), Is.EqualTo(54321));
        Assert.That(upgraded.Where(entry => entry!["Number"]!.GetValue<int>() is 23 or 24).Count(), Is.EqualTo(2));
        var once = feature.CustomConfiguration;

        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);

        Assert.That(configuration.Maps.Sum(map => map.ExitGates.Count), Is.EqualTo(gateCount + 2));
        Assert.That(feature.CustomConfiguration, Is.EqualTo(once));
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

    /// <summary>Repairs only the previous default wording, retaining owner configuration and gate identities.</summary>
    /// <param name="customDescription">Whether the owner has customized the description.</param>
    /// <param name="chineseNeutral">Whether the installed client uses Chinese in the neutral language too.</param>
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task VersionTwoRefinesOnlyOldDefaultDescriptionAsync(bool customDescription, bool chineseNeutral)
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new DataInitialization(provider, NullLoggerFactory.Instance).CreateInitialDataAsync(1, false).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var feature = configuration.PlugInConfigurations.Single(p => p.TypeId == typeof(LevelingWarpFeaturePlugIn).GUID);
        var document = JsonNode.Parse(feature.CustomConfiguration!)!;
        var destinations = document["Destinations"]!["$values"]!.AsArray();
        var entry = destinations.Single(d => d!["Number"]!.GetValue<int>() == 14)!;
        var chinese = CultureInfo.GetCultureInfo("zh-CN");
        var current = new LocalizedString(entry["Description"]!.GetValue<string>());
        Assert.That(current.GetTranslation(chinese), Is.EqualTo("五点练级区"));
        var oldDescription = new LocalizedString(chineseNeutral ? "五点" : "Five Spots").WithTranslation(chinese, customDescription ? "自定义海底练级点" : "五点").Value;
        entry["Description"] = oldDescription;
        entry["Costs"] = 55555;
        entry["IsEnabled"] = false;
        feature.IsActive = true;
        feature.CustomConfiguration = document.ToJsonString();
        var before = JsonNode.Parse(feature.CustomConfiguration)!;
        var update = new AddLevelingWarpNpcPlugIn();
        Assert.That(update.Version, Is.EqualTo(3));
        var installed = (await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Single(u => u.Key == update.Key);
        installed.Version = 2;
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(installed.Version, Is.EqualTo(3));
        var after = JsonNode.Parse(feature.CustomConfiguration!)!;
        var updatedEntry = after["Destinations"]!["$values"]!.AsArray().Single(d => d!["Number"]!.GetValue<int>() == 14)!;
        Assert.That(new LocalizedString(updatedEntry["Description"]!.GetValue<string>()).GetTranslation(chinese),
            Is.EqualTo(customDescription ? "自定义海底练级点" : "五点练级区"));
        Assert.That(new LocalizedString(updatedEntry["Description"]!.GetValue<string>()).ValueInNeutralLanguage,
            Is.EqualTo(chineseNeutral ? customDescription ? "五点" : "五点练级区" : "Five Spots"));
        Assert.That(feature.IsActive, Is.True);
        updatedEntry["Description"] = oldDescription;
        Assert.That(JsonNode.DeepEquals(before, after), Is.True, "Only the old default description may change.");
        var once = feature.CustomConfiguration;
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(feature.CustomConfiguration, Is.EqualTo(once));
    }

}
