// <copyright file="CreatorGoddessInitialization.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix;

using System.Text.Json;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence.Initialization.Updates;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Initializes Aria independently of the leveling teleport service, preserving operator customizations.
/// </summary>
public static class CreatorGoddessInitialization
{
    /// <summary>The safe street tile east of Noah, leaving space for the goddess's wings.</summary>
    public const byte NpcX = 147;

    /// <summary>The safe street tile south of the wall next to Noah.</summary>
    public const byte NpcY = 133;

    /// <summary>Ensures the decorative NPC, its placement and its greeting configuration.</summary>
    /// <param name="context">The persistence context.</param>
    /// <param name="configuration">The game configuration.</param>
    public static void EnsureConfiguration(IContext context, GameConfiguration configuration)
    {
        var npc = configuration.Monsters.SingleOrDefault(monster => monster.Number == CreatorGoddessNpcPlugIn.NpcNumber);
        if (npc is null)
        {
            npc = context.CreateNew<MonsterDefinition>();
            npc.SetGuid(CreatorGoddessNpcPlugIn.NpcNumber);
            npc.Number = CreatorGoddessNpcPlugIn.NpcNumber;
            npc.Designation = LocalizedString.FromResource(() => Properties.MonsterNames.CreatorGoddessAria);
            npc.ObjectKind = NpcObjectKind.PassiveNpc;
            npc.NpcWindow = NpcWindow.Undefined;
            configuration.Monsters.Add(npc);
        }

        if (!configuration.Maps.SelectMany(map => map.MonsterSpawns).Any(spawn => spawn.MonsterDefinition == npc))
        {
            var spawn = context.CreateNew<MonsterSpawnArea>();
            spawn.SetGuid(CreatorGoddessNpcPlugIn.NpcNumber);
            spawn.MonsterDefinition = npc;
            spawn.X1 = spawn.X2 = NpcX;
            spawn.Y1 = spawn.Y2 = NpcY;
            spawn.Quantity = 1;
            spawn.Direction = Direction.SouthEast;
            spawn.SpawnTrigger = SpawnTrigger.Automatic;
            configuration.Maps.Single(map => map.Number == 0).MonsterSpawns.Add(spawn);
        }

        var typeId = typeof(CreatorGoddessNpcPlugIn).GUID;
        var talk = configuration.PlugInConfigurations.SingleOrDefault(plugin => plugin.TypeId == typeId);
        if (talk is null)
        {
            talk = context.CreateNew<PlugInConfiguration>();
            talk.SetGuid(typeId);
            talk.TypeId = typeId;
            talk.IsActive = true;
            configuration.PlugInConfigurations.Add(talk);
        }

        // Fresh initialization discovers the talk plugin before the version-specific NPC exists.
        // Fill its unassigned reference without replacing an owner's NPC or activation setting.
        if (IsNpcUnassigned(talk.CustomConfiguration))
        {
            talk.SetConfiguration(
                new CreatorGoddessNpcPlugIn().CreateDefaultConfig(configuration),
                new ConfigureNpcTalkPlugInsPlugInBase.IdReferenceHandler());
        }
    }

    private static bool IsNpcUnassigned(string? customConfiguration)
    {
        if (string.IsNullOrEmpty(customConfiguration))
        {
            return true;
        }

        using var document = JsonDocument.Parse(customConfiguration);
        return !document.RootElement.TryGetProperty(nameof(NpcTalkPlugInConfiguration.Npc), out var npc)
               || npc.ValueKind == JsonValueKind.Null;
    }
}
