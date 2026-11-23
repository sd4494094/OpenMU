// <copyright file="MuExLesnarDialogueDataTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using System.Globalization;

using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.Persistence.Initialization.Updates;

/// <summary>
/// Verifies the optional MuEx town dialogue data.
/// </summary>
[TestFixture]
internal class MuExLesnarDialogueDataTest
{
    /// <summary>
    /// Checks the default and selected update paths and the original minimap coordinates.
    /// </summary>
    [Test]
    public async Task InstallsThreeTownPlacementsWhenSelectedAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new VersionSeasonSix.DataInitialization(provider, NullLoggerFactory.Instance)
            .CreateInitialDataAsync(1, false).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var update = new AddMuExLesnarDialogueUpdatePlugIn();
        Assert.That(update.IsMandatory, Is.False);
        Assert.That(update.RunsOnNewDatabase, Is.True);
        Assert.That(configuration.Monsters.Any(npc => npc.Number == 584), Is.False);
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Any(entry => entry.Key == update.Key), Is.False);
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);

        var npc = configuration.Monsters.Single(monster => monster.Number == 584);
        Assert.That(npc.ObjectKind, Is.EqualTo(NpcObjectKind.PassiveNpc));
        Assert.That(npc.NpcWindow, Is.EqualTo(NpcWindow.NpcDialog));
        Assert.That(npc.Designation.GetOwnTranslation(CultureInfo.GetCultureInfo("zh-CN")), Is.EqualTo("莱斯纳"));
        Assert.That(npc.MerchantStore, Is.Null);
        Assert.That(npc.ItemCraftings, Is.Empty);
        var placements = configuration.Maps.SelectMany(map => map.MonsterSpawns)
            .Where(spawn => spawn.MonsterDefinition == npc).OrderBy(spawn => spawn.GameMap!.Number).ToArray();
        Assert.That(placements.Select(spawn => ((int)spawn.GameMap!.Number, (int)spawn.X1, (int)spawn.Y1)),
            Is.EqualTo(new[] { (0, 114, 132), (2, 240, 53), (3, 183, 126) }));
        Assert.That(placements.All(spawn => spawn.X1 == spawn.X2 && spawn.Y1 == spawn.Y2
            && spawn.Quantity == 1 && spawn.SpawnTrigger == SpawnTrigger.Automatic), Is.True);
        Assert.That(placements.Select(spawn => spawn.GetId()).Distinct().Count(), Is.EqualTo(3));
    }

    /// <summary>
    /// Preserves the administrator's definition and placement, including incompatible roles.
    /// </summary>
    /// <param name="window">The NPC window used by the administrator.</param>
    /// <param name="addsMissingPlacement">Whether the existing definition can display the dialogue.</param>
    [TestCase(NpcWindow.NpcDialog, true)]
    [TestCase(NpcWindow.Undefined, false)]
    public async Task PreservesExistingDefinitionAndPlacementAsync(NpcWindow window, bool addsMissingPlacement)
    {
        var provider = new InMemoryPersistenceContextProvider();
        using var context = provider.CreateNewContext();
        var configuration = context.CreateNew<GameConfiguration>();
        var existing = context.CreateNew<MonsterDefinition>();
        existing.Number = 584;
        existing.Designation = "Custom resident";
        existing.ObjectKind = NpcObjectKind.PassiveNpc;
        existing.NpcWindow = window;
        configuration.Monsters.Add(existing);
        var originalMap = context.CreateNew<GameMapDefinition>();
        var otherMap = context.CreateNew<GameMapDefinition>();
        originalMap.Number = 0;
        otherMap.Number = 2;
        configuration.Maps.Add(originalMap);
        configuration.Maps.Add(otherMap);
        var placement = context.CreateNew<MonsterSpawnArea>();
        placement.MonsterDefinition = existing;
        placement.GameMap = originalMap;
        placement.X1 = placement.X2 = 42;
        originalMap.MonsterSpawns.Add(placement);

        await new AddMuExLesnarDialogueUpdatePlugIn().ApplyUpdateAsync(context, configuration).ConfigureAwait(false);

        Assert.That(configuration.Monsters.Single(), Is.SameAs(existing));
        Assert.That(existing.Designation.ValueInNeutralLanguage, Is.EqualTo("Custom resident"));
        Assert.That(existing.NpcWindow, Is.EqualTo(window));
        Assert.That(originalMap.MonsterSpawns.Single(), Is.SameAs(placement));
        Assert.That(placement.X1, Is.EqualTo(42));
        Assert.That(otherMap.MonsterSpawns.Count, Is.EqualTo(addsMissingPlacement ? 1 : 0));
    }
}
