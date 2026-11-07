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

        // Preserve custom NPCs and already configured placements.
        if (npc.NpcWindow == NpcWindow.StatReInitializer
            && gameConfiguration.Maps.FirstOrDefault(map => map.Number == 0) is { } lorencia
            && !lorencia.MonsterSpawns.Any(spawn => spawn.MonsterDefinition == npc))
        {
            var spawn = context.CreateNew<MonsterSpawnArea>();
            spawn.MonsterDefinition = npc;
            spawn.X1 = spawn.X2 = 128;
            spawn.Y1 = spawn.Y2 = 126;
            spawn.Quantity = 1;
            spawn.Direction = Direction.SouthEast;
            spawn.SpawnTrigger = SpawnTrigger.Automatic;
            lorencia.MonsterSpawns.Add(spawn);
        }

        return ValueTask.CompletedTask;
    }
}
