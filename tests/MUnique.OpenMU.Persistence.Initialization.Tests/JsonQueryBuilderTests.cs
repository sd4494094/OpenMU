// <copyright file="JsonQueryBuilderTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using System.Diagnostics;
using System.IO;
using Microsoft.EntityFrameworkCore;
using MUnique.OpenMU.Persistence.EntityFramework;
using MUnique.OpenMU.Persistence.EntityFramework.Json;
using MUnique.OpenMU.Testing;
using Account = MUnique.OpenMU.Persistence.EntityFramework.Model.Account;
using GameConfiguration = MUnique.OpenMU.Persistence.EntityFramework.Model.GameConfiguration;

/// <summary>
/// Tests for the <see cref="JsonQueryBuilder"/>.
/// </summary>
[TestFixture]
internal class JsonQueryBuilderTests
{
    /// <summary>
    /// Sets up this instance.
    /// </summary>
    [OneTimeSetUp]
    public void Setup()
    {
        if (!ConnectionConfigurator.IsInitialized)
        {
            ConnectionConfigurator.Initialize(new ConfigFileDatabaseConnectionStringProvider());
        }
    }

    /// <summary>
    /// Tests the json query builder for the <see cref="GameConfiguration"/> type.
    /// </summary>
    [Test]
    public void JsonQueryBuilderGameConfiguration()
    {
        using var installationContext = new ConfigurationContext();
        var type = installationContext.Model.GetEntityTypes().FirstOrDefault(t => t.ClrType == typeof(GameConfiguration));
        string result;
        Stopwatch stopwatch = new();
        stopwatch.Start();
        try
        {
            var builder = new GameConfigurationJsonQueryBuilder();
            result = builder.BuildJsonQueryForEntity(type!);
        }
        finally
        {
            stopwatch.Stop();
        }

        result = $"-- Json query created in {stopwatch.ElapsedMilliseconds} ms:{Environment.NewLine}" + result;
        //// File.WriteAllText(@"C:\temp\json_GameConfiguration.txt", result);
    }

    /// <summary>
    /// Tests the json query builder for the <see cref="Account"/> type.
    /// </summary>
    [Test]
    public void JsonQueryBuilderAccount()
    {
        using var installationContext = new ConfigurationContext();
        var type = installationContext.Model.GetEntityTypes().FirstOrDefault(t => t.ClrType == typeof(Account));
        string result;
        Stopwatch stopwatch = new();
        stopwatch.Start();
        try
        {
            var builder = new JsonQueryBuilder();
            result = builder.BuildJsonQueryForEntity(type!);
        }
        finally
        {
            stopwatch.Stop();
        }

        result = $"-- Json query created in {stopwatch.ElapsedMilliseconds} ms:{Environment.NewLine}" + result;
        //// File.WriteAllText(@"C:\temp\json_Account.txt", result);
    }

    /// <summary>
    /// Loads the <see cref="GameConfiguration"/> using the <see cref="JsonQueryBuilder"/> and the <see cref="JsonObjectLoader"/>.
    /// Checks the loaded configuration graph; elapsed time is diagnostic rather than a functional requirement.
    /// </summary>
    [Test]
    [Explicit("Requires OPENMU_INTEGRATION_TEST_CONNECTION pointing to a disposable local PostgreSQL cluster.")]
    [NonParallelizable]
    public async Task LoadConfigByJsonAsync()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(initializeConfiguration: true).ConfigureAwait(false);
        await using var installationContext = new ConfigurationContext();
        await installationContext.Database.OpenConnectionAsync().ConfigureAwait(false);
        var builder = new GameConfigurationJsonObjectLoader();
        IEnumerable<GameConfiguration> result;
        Stopwatch stopwatch = new();
        stopwatch.Start();
        try
        {
            result = await builder.LoadAllObjectsAsync<EntityFramework.Model.GameConfiguration>(installationContext).ConfigureAwait(false);
            result = result.ToList();
        }
        finally
        {
            stopwatch.Stop();
        }

        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.Not.EqualTo(0));
        var configuration = result.Single();
        Assert.That(configuration.Items, Is.Not.Empty);
        Assert.That(configuration.Maps, Is.Not.Empty);
        Assert.That(configuration.CharacterClasses, Is.Not.Empty);
        TestContext.Progress.WriteLine($"Loaded configuration through JSON in {stopwatch.ElapsedMilliseconds} ms.");
    }
}
