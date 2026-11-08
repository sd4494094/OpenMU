// <copyright file="LevelingWarpInitialization.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.LevelingWarp;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence.Initialization.Updates;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Shared fresh-data and incremental initialization for the optional NPC service.
/// </summary>
public static class LevelingWarpInitialization
{
    /// <summary>The dedicated NPC number, reserved in the MuMain monster database.</summary>
    public const short NpcNumber = 710;

    /// <summary>The default town-street X coordinate.</summary>
    public const byte NpcX = 143;

    /// <summary>The default town-street Y coordinate.</summary>
    public const byte NpcY = 131;

    private const int YetiRoomOneNumber = 23;
    private const int YetiRoomTwoNumber = 24;

    /// <summary>Ensures the NPC and its outdoor spawn without altering existing placements.</summary>
    /// <param name="context">The configuration context.</param>
    /// <param name="configuration">The configuration.</param>
    public static void EnsureNpc(IContext context, GameConfiguration configuration)
    {
        var npc = configuration.Monsters.SingleOrDefault(n => n.Number == NpcNumber);
        if (npc is null)
        {
            npc = context.CreateNew<MonsterDefinition>();
            npc.SetGuid(NpcNumber);
            npc.Number = NpcNumber;
            npc.Designation = LocalizedString.FromResource(() => Properties.MonsterNames.LevelingWarpGuide);
            npc.ObjectKind = NpcObjectKind.PassiveNpc;
            npc.NpcWindow = NpcWindow.Undefined;
            configuration.Monsters.Add(npc);
        }

        var map = configuration.Maps.Single(m => m.Number == 0);
        if (!map.MonsterSpawns.Any(s => s.MonsterDefinition == npc))
        {
            var spawn = context.CreateNew<MonsterSpawnArea>();
            spawn.MonsterDefinition = npc;
            spawn.X1 = spawn.X2 = NpcX;
            spawn.Y1 = spawn.Y2 = NpcY;
            spawn.Quantity = 1;
            spawn.Direction = Direction.SouthEast;
            spawn.SpawnTrigger = SpawnTrigger.Automatic;
            map.MonsterSpawns.Add(spawn);
        }
    }

    /// <summary>Initializes plugin defaults only when they do not already contain owner data.</summary>
    /// <param name="context">The configuration context.</param>
    /// <param name="configuration">The configuration.</param>
    public static void EnsureConfiguration(IContext context, GameConfiguration configuration)
    {
        EnsureNpc(context, configuration);
        var feature = GetOrCreate(context, configuration, typeof(LevelingWarpFeaturePlugIn).GUID);

        // Default auto-created plugin configuration is empty; an already configured owner list is preserved.
        if (string.IsNullOrEmpty(feature.CustomConfiguration) || HasNoDestinations(feature.CustomConfiguration))
        {
            feature.SetConfiguration(CreateDestinations(context, configuration), new ConfigureNpcTalkPlugInsPlugInBase.IdReferenceHandler());
        }

        RefineDefaultDescription(feature);
        EnsureYetiDestinations(context, configuration, feature);

        var talk = GetOrCreate(context, configuration, typeof(LevelingWarpNpcPlugIn).GUID);
        if (string.IsNullOrEmpty(talk.CustomConfiguration) || HasNoNpc(talk.CustomConfiguration))
        {
            talk.SetConfiguration(new LevelingWarpNpcPlugIn().CreateDefaultConfig(configuration), new ConfigureNpcTalkPlugInsPlugInBase.IdReferenceHandler());
        }
    }

    /// <summary>Creates default destinations and independent non-respawn gates.</summary>
    /// <param name="context">The context.</param>
    /// <param name="configuration">The map configuration.</param>
    /// <returns>The optional service configuration.</returns>
    public static LevelingWarpConfiguration CreateDestinations(IContext context, GameConfiguration configuration)
    {
        var result = new LevelingWarpConfiguration();
        Add(context, configuration, result, 1, 1, "Dungeon Floor 1", "地下城一层", "Ghost Room", "幽灵房", 44, 185, 8);
        Add(context, configuration, result, 2, 1, "Dungeon Floor 1", "地下城一层", "End of Floor", "一层尽头", 237, 171, 8);
        Add(context, configuration, result, 3, 1, "Dungeon Floor 2", "地下城二层", "Prison", "地牢", 162, 120, 9);
        Add(context, configuration, result, 4, 1, "Dungeon Floor 2", "地下城二层", "Hall", "大厅", 235, 13, 9);
        Add(context, configuration, result, 5, 1, "Dungeon Floor 2", "地下城二层", "Poison Bull Room", "毒牛房", 115, 49, 9);
        Add(context, configuration, result, 6, 4, "Lost Tower Floor 3", "失落之塔三层", "Death Cow Room", "牛房", 92, 172, 16);
        Add(context, configuration, result, 7, 4, "Lost Tower Floor 3", "失落之塔三层", "Golden Spot No. 2", "二号黄金点", 95, 189, 16);
        Add(context, configuration, result, 8, 4, "Lost Tower Floor 3", "失落之塔三层", "Golden Spot No. 3", "三号黄金点", 124, 168, 16);
        Add(context, configuration, result, 9, 4, "Lost Tower Floor 5", "失落之塔五层", "Entrance to Floor 6", "通往六层的入口", 120, 7, 18);
        Add(context, configuration, result, 10, 4, "Lost Tower Floor 6", "失落之塔六层", "Crossroads", "十字路口", 24, 17, 19);
        Add(context, configuration, result, 11, 4, "Lost Tower Floor 7", "失落之塔七层", "Corner No. 1", "一号拐角", 45, 87, 20);
        Add(context, configuration, result, 12, 4, "Lost Tower Floor 7", "失落之塔七层", "Hall", "大厅", 14, 103, 20);
        Add(context, configuration, result, 13, 4, "Lost Tower Floor 7", "失落之塔七层", "Corner No. 2", "二号拐角", 50, 112, 20);
        Add(context, configuration, result, 14, 7, "Atlans Area 1", "亚特兰蒂斯一区", "Five Spots", "五点练级区", 71, 18, 11);
        Add(context, configuration, result, 15, 7, "Atlans", "亚特兰蒂斯", "Shallow Magic Area", "浅海魔区", 224, 187, 13);
        Add(context, configuration, result, 16, 8, "Tarkan", "死亡沙漠", "Iron Wheel Area", "铁轮战士聚集点", 59, 71, 21);
        Add(context, configuration, result, 17, 8, "Tarkan", "死亡沙漠", "Death Beam Knight Area", "破坏骑士聚集点", 56, 126, 21);
        Add(context, configuration, result, 18, 8, "Tarkan", "死亡沙漠", "Tantalos Area", "黑炎魔聚集点", 155, 190, 22);
        Add(context, configuration, result, 19, 10, "Icarus", "天空之城", "Alquamos Area", "蓝魔龙聚集点", 31, 74, 23);
        Add(context, configuration, result, 20, 10, "Icarus", "天空之城", "Mega Crust Area", "恶灵王聚集点", 30, 100, 23);
        Add(context, configuration, result, 21, 10, "Icarus", "天空之城", "Phantom Knight Area", "幻影骑士聚集点", 40, 133, 23);
        Add(context, configuration, result, 22, 10, "Icarus", "天空之城", "Drakan Area", "红魔龙聚集点", 47, 190, 23);
        Add(context, configuration, result, YetiRoomOneNumber, 2, "Devias", "冰风谷", "Yeti Room No. 1", "雪人房1号", 195, 165, 4);
        Add(context, configuration, result, YetiRoomTwoNumber, 2, "Devias", "冰风谷", "Yeti Room No. 2", "雪人房2号", 37, 25, 4);
        Add(context, configuration, result, 101, 7, "Atlans", "亚特兰蒂斯", "Boss: Hydra", "首领·海魔希特拉", 224, 203, 13);
        Add(context, configuration, result, 102, 8, "Tarkan", "死亡沙漠", "Boss: Zaikan", "首领·魔王扎坎", 6, 241, 22);
        Add(context, configuration, result, 103, 8, "Tarkan", "死亡沙漠", "Boss: Death Beam Knight", "首领·炽炎魔", 156, 225, 22);
        Add(context, configuration, result, 104, 10, "Icarus", "天空之城", "Boss: Phoenix of Darkness", "首领·天魔菲尼斯", 30, 235, 23);
        return result;
    }

    private static void EnsureYetiDestinations(IContext context, GameConfiguration configuration, PlugInConfiguration feature)
    {
        var document = JsonNode.Parse(feature.CustomConfiguration!);
        var destinations = GetDestinationArray(document);
        if (destinations is null)
        {
            return;
        }

        var existingNumbers = destinations
            .Where(entry => entry?["Number"] is not null)
            .Select(entry => entry!["Number"]!.GetValue<int>())
            .ToHashSet();
        var missingNumbers = new[] { YetiRoomOneNumber, YetiRoomTwoNumber }
            .Where(number => !existingNumbers.Contains(number))
            .ToArray();
        if (missingNumbers.Length == 0)
        {
            return;
        }

        var additions = new LevelingWarpConfiguration();
        if (missingNumbers.Contains(YetiRoomOneNumber))
        {
            Add(context, configuration, additions, YetiRoomOneNumber, 2, "Devias", "冰风谷", "Yeti Room No. 1", "雪人房1号", 195, 165, 4);
        }

        if (missingNumbers.Contains(YetiRoomTwoNumber))
        {
            Add(context, configuration, additions, YetiRoomTwoNumber, 2, "Devias", "冰风谷", "Yeti Room No. 2", "雪人房2号", 37, 25, 4);
        }

        var serialized = new PlugInConfiguration();
        serialized.SetConfiguration(additions, new ConfigureNpcTalkPlugInsPlugInBase.IdReferenceHandler());
        var serializedDestinations = GetDestinationArray(JsonNode.Parse(serialized.CustomConfiguration!))!;
        foreach (var entry in serializedDestinations)
        {
            destinations.Add(entry?.DeepClone());
        }

        feature.CustomConfiguration = document!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonArray? GetDestinationArray(JsonNode? document)
    {
        var destinations = document?["Destinations"];
        return destinations is JsonObject ? destinations["$values"] as JsonArray : destinations as JsonArray;
    }

    private static void RefineDefaultDescription(PlugInConfiguration feature)
    {
        var document = JsonNode.Parse(feature.CustomConfiguration!);
        var entries = document?["Destinations"];
        if (entries is JsonObject)
        {
            entries = entries["$values"];
        }

        if (entries is not JsonArray destinations)
        {
            return;
        }

        // Match only the released default text and stable destination number, preserving owner settings and references.
        var chinese = CultureInfo.GetCultureInfo("zh-CN");
        var oldDescription = new LocalizedString("Five Spots").WithTranslation(chinese, "五点").Value;
        var oldChineseDescription = new LocalizedString("五点").WithTranslation(chinese, "五点").Value;
        foreach (var entry in destinations)
        {
            if (entry?["Number"]?.GetValue<int>() == 14
                && entry["Description"] is JsonValue description
                && description.TryGetValue<string>(out var text)
                && (text == oldDescription || text == oldChineseDescription))
            {
                var neutralDescription = text == oldChineseDescription ? "五点练级区" : "Five Spots";
                entry["Description"] = new LocalizedString(neutralDescription).WithTranslation(chinese, "五点练级区").Value;
                feature.CustomConfiguration = document!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            }
        }
    }

    private static bool HasNoNpc(string json)
    {
        using var document = JsonDocument.Parse(json);
        return !document.RootElement.TryGetProperty("Npc", out var npc) || npc.ValueKind == JsonValueKind.Null;
    }

    private static bool HasNoDestinations(string json)
    {
        using var document = JsonDocument.Parse(json);
        var entries = document.RootElement.GetProperty("Destinations");
        if (entries.ValueKind == JsonValueKind.Object)
        {
            entries = entries.GetProperty("$values");
        }

        return entries.GetArrayLength() == 0;
    }

    private static PlugInConfiguration GetOrCreate(IContext context, GameConfiguration config, Guid type)
    {
        var existing = config.PlugInConfigurations.SingleOrDefault(p => p.TypeId == type);
        if (existing is not null)
        {
            return existing;
        }

        var entry = context.CreateNew<PlugInConfiguration>();
        entry.SetGuid(type);
        entry.TypeId = type;
        entry.IsActive = false;
        config.PlugInConfigurations.Add(entry);
        return entry;
    }

    private static void Add(IContext context, GameConfiguration config, LevelingWarpConfiguration result, int number, short mapNumber, string name, string chineseName, string description, string chineseDescription, byte x, byte y, int publicWarpIndex)
    {
        var map = config.Maps.Single(m => m.Number == mapNumber);
        var baseline = config.WarpList.Single(w => w.Index == publicWarpIndex && w.Gate?.Map == map);
        var gate = context.CreateNew<ExitGate>();
        gate.Map = map;
        gate.X1 = gate.X2 = x;
        gate.Y1 = gate.Y2 = y;
        gate.Direction = Direction.South;
        gate.IsSpawnGate = false;
        map.ExitGates.Add(gate);
        result.Destinations.Add(new LevelingWarpDestination
        {
            Number = number,
            Name = new LocalizedString(name).WithTranslation(CultureInfo.GetCultureInfo("zh-CN"), chineseName),
            Description = new LocalizedString(description).WithTranslation(CultureInfo.GetCultureInfo("zh-CN"), chineseDescription),
            Gate = gate,
            LevelRequirement = baseline.LevelRequirement,
            Costs = baseline.Costs,
        });
    }
}
