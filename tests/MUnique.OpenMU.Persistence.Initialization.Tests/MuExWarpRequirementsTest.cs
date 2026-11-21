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
    [Test]
    public async Task UpdatesOnlyTheTwoSelectedTravelRequirementsAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new VersionSeasonSix.DataInitialization(provider, NullLoggerFactory.Instance)
            .CreateInitialDataAsync(1, true).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var update = new SetMuExWarpRequirementsUpdatePlugIn();
        var before = configuration.WarpList.OrderBy(route => route.Index)
            .Select(route => (route.Index, route.LevelRequirement, route.Costs, route.Gate)).ToArray();
        var expected = before.Select(route => route.Index switch
        {
            26 => (route.Index, 10, 0, route.Gate),
            44 => (route.Index, 1, 10_000, route.Gate),
            _ => route,
        }).ToArray();

        Assert.That(before.Where(route => route.Index is 26 or 44)
            .Select(route => (route.Index, route.LevelRequirement, route.Costs, route.Gate?.Map?.Number)),
            Is.EqualTo(new (int, int, int, byte?)[] { (26, 190, 10_000, 34), (44, 200, 18_000, 79) }));
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Any(entry => entry.Key == update.Key), Is.False);

        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(configuration.WarpList.OrderBy(route => route.Index)
            .Select(route => (route.Index, route.LevelRequirement, route.Costs, route.Gate)), Is.EqualTo(expected));

        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(configuration.WarpList.OrderBy(route => route.Index)
            .Select(route => (route.Index, route.LevelRequirement, route.Costs, route.Gate)), Is.EqualTo(expected));
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Single(entry => entry.Key == update.Key).Version, Is.EqualTo(1));
    }

    /// <summary>
    /// A conflicting destination prevents either route from being modified.
    /// </summary>
    [Test]
    public void RejectsAnUnexpectedDestinationBeforeMutatingEitherRoute()
    {
        var provider = new InMemoryPersistenceContextProvider();
        using var context = provider.CreateNewContext();
        var configuration = context.CreateNew<GameConfiguration>();
        var crywolf = context.CreateNew<WarpInfo>();
        crywolf.Index = 26;
        crywolf.LevelRequirement = 190;
        crywolf.Costs = 10_000;
        var gate = context.CreateNew<ExitGate>();
        var map = context.CreateNew<GameMapDefinition>();
        map.Number = 34;
        gate.Map = map;
        crywolf.Gate = gate;
        configuration.WarpList.Add(crywolf);
        var market = context.CreateNew<WarpInfo>();
        market.Index = 44;
        market.Gate = gate;
        configuration.WarpList.Add(market);

        var update = new SetMuExWarpRequirementsUpdatePlugIn();
        Assert.ThrowsAsync<InvalidOperationException>(() => update.ApplyUpdateAsync(context, configuration).AsTask());
        Assert.That((crywolf.LevelRequirement, crywolf.Costs), Is.EqualTo((190, 10_000)));
    }
}
