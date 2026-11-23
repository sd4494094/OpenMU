// <copyright file="AddMuExLesnarDialogueUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Enables the MuEx Lesnar dialogue at the three marked locations in the original client.
/// </summary>
[PlugIn]
[Display(Name = nameof(PlugInResources.AddMuExLesnarDialogue_Name), Description = nameof(PlugInResources.AddMuExLesnarDialogue_Description), ResourceType = typeof(PlugInResources))]
[Guid("98c64cb3-fc24-490a-b2db-002aba736f1e")]
public sealed class AddMuExLesnarDialogueUpdatePlugIn : UpdatePlugInBase
{
    private const short NpcNumber = 584;

    // MapCharacters.bmd lists this NPC in these towns. Their minimaps supply the marked tiles.
    private static readonly (short Map, byte X, byte Y)[] TownMarkers = [(0, 114, 132), (2, 240, 53), (3, 183, 126)];

    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override string Name => PlugInResources.AddMuExLesnarDialogue_Name;

    /// <inheritdoc />
    public override string Description => PlugInResources.AddMuExLesnarDialogue_Description;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 11, 23, 9, 36, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    public override bool RunsOnNewDatabase => true;

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        var npc = gameConfiguration.Monsters.FirstOrDefault(monster => monster.Number == NpcNumber);
        if (npc is not null && npc is not { ObjectKind: NpcObjectKind.PassiveNpc, NpcWindow: NpcWindow.NpcDialog, MerchantStore: null })
        {
            return ValueTask.CompletedTask;
        }

        foreach (var (number, x, y) in TownMarkers)
        {
            var map = gameConfiguration.Maps.FirstOrDefault(candidate => candidate.Number == number);
            if (map is null || map.MonsterSpawns.Any(spawn => spawn.MonsterDefinition?.Number == NpcNumber))
            {
                continue;
            }

            if (npc is null)
            {
                npc = context.CreateNew<MonsterDefinition>();
                npc.Number = NpcNumber;
                npc.Designation = LocalizedString.FromResource(() => MonsterNames.MuExLesnar);
                npc.ObjectKind = NpcObjectKind.PassiveNpc;
                npc.NpcWindow = NpcWindow.NpcDialog;
                npc.SetGuid(NpcNumber);
                gameConfiguration.Monsters.Add(npc);
            }

            var placement = context.CreateNew<MonsterSpawnArea>();
            placement.SetGuid(number, NpcNumber, 1);
            placement.GameMap = map;
            placement.MonsterDefinition = npc;
            placement.X1 = placement.X2 = x;
            placement.Y1 = placement.Y2 = y;
            placement.Quantity = 1;
            placement.SpawnTrigger = SpawnTrigger.Automatic;
            map.MonsterSpawns.Add(placement);
        }

        return ValueTask.CompletedTask;
    }
}
