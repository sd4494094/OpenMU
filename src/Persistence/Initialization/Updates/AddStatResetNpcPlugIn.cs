// <copyright file="AddStatResetNpcPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Adds the native stat reset NPC to existing Season 6 configurations without enabling the optional feature.
/// </summary>
[PlugIn]
[Guid("108ae690-0a2e-444b-a36a-938ee8268aaa")]
[Display(Name = nameof(Properties.PlugInResources.AddStatResetNpcPlugIn_Name), Description = nameof(Properties.PlugInResources.AddStatResetNpcPlugIn_Description), ResourceType = typeof(Properties.PlugInResources))]
public class AddStatResetNpcPlugIn : UpdatePlugInBase
{
    /// <summary>
    /// The X coordinate shipped by version 1, inside the tavern.
    /// </summary>
    private const byte OriginalSpawnX = 128;

    /// <summary>
    /// The Y coordinate shipped by version 1, inside the tavern.
    /// </summary>
    private const byte OriginalSpawnY = 126;

    /// <inheritdoc />
    public override string Name => Properties.PlugInResources.AddStatResetNpcPlugIn_Name;

    /// <inheritdoc />
    public override string Description => Properties.PlugInResources.AddStatResetNpcPlugIn_Description;

    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override int Version => 2;

    /// <inheritdoc />
    public override DateTime UpdatedAt => new(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        var npc = gameConfiguration.Monsters.FirstOrDefault(monster => monster.Number == 464);
        if (npc is null)
        {
            npc = context.CreateNew<MonsterDefinition>();
            npc.Number = 464;
            npc.Designation = LocalizedString.FromResource(() => Properties.MonsterNames.ErinStatResetHelper);
            npc.ObjectKind = NpcObjectKind.PassiveNpc;
            npc.NpcWindow = NpcWindow.StatReInitializer;
            gameConfiguration.Monsters.Add(npc);
        }

        if (npc.NpcWindow == NpcWindow.StatReInitializer
            && gameConfiguration.Maps.FirstOrDefault(map => map.Number == 0) is { } lorencia)
        {
            EnsureOutdoorSpawn(context, lorencia, npc);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Adds the outdoor placement or moves only the unmodified version 1 placement.
    /// </summary>
    /// <param name="context">The persistence context.</param>
    /// <param name="map">The Lorencia definition.</param>
    /// <param name="npc">The stat reset helper.</param>
    private static void EnsureOutdoorSpawn(IContext context, GameMapDefinition map, MonsterDefinition npc)
    {
        var spawns = map.MonsterSpawns.Where(spawn => spawn.MonsterDefinition == npc).ToArray();
        if (spawns.Length == 0)
        {
            var spawn = context.CreateNew<MonsterSpawnArea>();
            spawn.MonsterDefinition = npc;
            spawn.X1 = spawn.X2 = VersionSeasonSix.NpcInitialization.StatResetNpcX;
            spawn.Y1 = spawn.Y2 = VersionSeasonSix.NpcInitialization.StatResetNpcY;
            spawn.Quantity = 1;
            spawn.Direction = Direction.SouthEast;
            spawn.SpawnTrigger = SpawnTrigger.Automatic;
            map.MonsterSpawns.Add(spawn);
            return;
        }

        // Preserve owner customizations, including additional spawns, areas, direction and trigger.
        if (spawns is [var original]
            && original.X1 == OriginalSpawnX && original.X2 == OriginalSpawnX
            && original.Y1 == OriginalSpawnY && original.Y2 == OriginalSpawnY
            && original.Quantity == 1 && original.Direction == Direction.SouthEast
            && original.SpawnTrigger == SpawnTrigger.Automatic)
        {
            original.X1 = original.X2 = VersionSeasonSix.NpcInitialization.StatResetNpcX;
            original.Y1 = original.Y2 = VersionSeasonSix.NpcInitialization.StatResetNpcY;
        }
    }
}
