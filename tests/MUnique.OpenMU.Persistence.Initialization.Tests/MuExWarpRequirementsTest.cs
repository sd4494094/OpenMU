// <copyright file="MuExWarpRequirementsTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.Persistence.Initialization.Updates;

/// <summary>
/// Verifies the optional MuEx travel requirements on a Season 6 configuration.
/// </summary>
[TestFixture]
internal class MuExWarpRequirementsTest
{
    /// <summary>
    /// Verifies defaults, opt-in, and repeated application of the update.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task UpdatesOnlySelectedMenuRoutesAndKeepsSharedGatesAsync(bool versionOneInstalled)
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new VersionSeasonSix.DataInitialization(provider, NullLoggerFactory.Instance)
            .CreateInitialDataAsync(1, true).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var update = new SetMuExWarpRequirementsUpdatePlugIn();
        var routes = configuration.WarpList.OrderBy(route => route.Index).ToArray();
        var before = routes.Select(route => (route.LevelRequirement, route.Costs, route.Gate)).ToArray();
        var menuLandings = new (int Index, byte Map, byte X1, byte Y1, byte X2, byte Y2)[]
        {
            (1, 6, 72, 140, 73, 181),
            (21, 8, 189, 63, 205, 72),
            (22, 8, 96, 143, 100, 146),
        };
        var originalGates = menuLandings.Select(entry => routes.Single(route => route.Index == entry.Index).Gate!).ToArray();
        var originalBounds = originalGates.Select(gate => (gate.X1, gate.Y1, gate.X2, gate.Y2, gate.IsSpawnGate)).ToArray();
        var arena = routes.Single(route => route.Index == 1);
        var otherMenu = context.CreateNew<WarpInfo>();
        otherMenu.Index = 1000;
        otherMenu.Gate = arena.Gate;
        configuration.WarpList.Add(otherMenu);

        if (versionOneInstalled)
        {
            // Simulate the persisted state of the original release.
            var entry = context.CreateNew<ConfigurationUpdate>();
            entry.Key = update.Key;
            entry.Version = 1;
            entry.InstalledAt = DateTime.UtcNow;
            var crywolf = routes.Single(route => route.Index == 26);
            (crywolf.LevelRequirement, crywolf.Costs) = (10, 0);
            var market = routes.Single(route => route.Index == 44);
            (market.LevelRequirement, market.Costs) = (1, 10_000);
        }

        Assert.That(originalBounds, Is.EqualTo(new (byte, byte, byte, byte, bool)[]
        {
            (101, 115, 103, 117, true),
            (187, 63, 203, 69, true),
            (91, 160, 93, 161, false),
        }));
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Any(entry => entry.Key == update.Key), Is.EqualTo(versionOneInstalled));
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);

        foreach (var (index, map, x1, y1, x2, y2) in menuLandings)
        {
            var target = routes.Single(route => route.Index == index).Gate!;
            Assert.That((target.Map!.Number, target.X1, target.Y1, target.X2, target.Y2, target.IsSpawnGate),
                Is.EqualTo((map, x1, y1, x2, y2, false)));
            Assert.That(target.Map.ExitGates, Does.Contain(target));
        }

        Assert.That(originalGates.Select(gate => (gate.X1, gate.Y1, gate.X2, gate.Y2, gate.IsSpawnGate)), Is.EqualTo(originalBounds));
        Assert.That(otherMenu.Gate, Is.SameAs(originalGates[0]));
        Assert.That(routes.Select(route => (route.LevelRequirement, route.Costs)),
            Is.EqualTo(routes.Select((route, i) => route.Index switch
            {
                26 => (10, 0),
                44 => (1, 10_000),
                _ => (before[i].LevelRequirement, before[i].Costs),
            })));
        Assert.That(routes.Where(route => route.Index is not 1 and not 21 and not 22).Select(route => route.Gate),
            Is.EqualTo(routes.Select((route, i) => (route, before[i].Gate))
                .Where(pair => pair.route.Index is not 1 and not 21 and not 22).Select(pair => pair.Gate)));

        var installedGates = routes.Select(route => route.Gate).ToArray();
        var counts = configuration.Maps.Select(map => map.ExitGates.Count).ToArray();
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(routes.Select(route => route.Gate), Is.EqualTo(installedGates));
        Assert.That(configuration.Maps.Select(map => map.ExitGates.Count), Is.EqualTo(counts));
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Single(entry => entry.Key == update.Key).Version, Is.EqualTo(2));
    }

    /// <summary>
    /// A conflicting destination prevents the menu routes from being modified.
    /// </summary>
    [Test]
    public void RejectsAnUnexpectedDestinationBeforeMutatingAnyRoute()
    {
        var provider = new InMemoryPersistenceContextProvider();
        using var context = provider.CreateNewContext();
        var configuration = context.CreateNew<GameConfiguration>();
        var arena = AddRoute(1, 6);
        var tarkan = AddRoute(21, 8);
        var tarkan2 = AddRoute(22, 8);
        var crywolf = AddRoute(26, 34);
        crywolf.LevelRequirement = 190;
        crywolf.Costs = 10_000;
        AddRoute(44, 34);
        var gates = new[] { arena.Gate, tarkan.Gate, tarkan2.Gate };

        var update = new SetMuExWarpRequirementsUpdatePlugIn();
        Assert.ThrowsAsync<InvalidOperationException>(() => update.ApplyUpdateAsync(context, configuration).AsTask());
        Assert.That(new[] { arena.Gate, tarkan.Gate, tarkan2.Gate }, Is.EqualTo(gates));
        Assert.That((crywolf.LevelRequirement, crywolf.Costs), Is.EqualTo((190, 10_000)));

        WarpInfo AddRoute(int index, byte mapNumber)
        {
            var route = context.CreateNew<WarpInfo>();
            route.Index = index;
            var gate = context.CreateNew<ExitGate>();
            var map = context.CreateNew<GameMapDefinition>();
            map.Number = mapNumber;
            gate.Map = map;
            route.Gate = gate;
            configuration.WarpList.Add(route);
            return route;
        }
    }
}
