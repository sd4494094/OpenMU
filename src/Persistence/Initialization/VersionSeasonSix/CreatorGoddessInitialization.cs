// <copyright file="CreatorGoddessInitialization.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix;

using System.Globalization;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.NPC;
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
            var designation = LocalizedString.FromResource(() => Properties.MonsterNames.CreatorGoddessAria);

            // Existing MuMain accounts use English on the wire while this deployment displays Chinese.
            npc.Designation = designation.WithTranslation(
                CultureInfo.GetCultureInfo("en"),
                designation.GetTranslation(CultureInfo.GetCultureInfo("zh-CN")) ?? designation.ToString() ?? string.Empty);
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
            talk.SetConfiguration(
                new CreatorGoddessNpcPlugIn().CreateDefaultConfig(configuration),
                new ConfigureNpcTalkPlugInsPlugInBase.IdReferenceHandler());
            configuration.PlugInConfigurations.Add(talk);
        }
    }
}
