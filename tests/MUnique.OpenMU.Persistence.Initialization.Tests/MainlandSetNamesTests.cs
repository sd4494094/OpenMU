// <copyright file="MainlandSetNamesTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.Persistence.Initialization.Captions;
using MUnique.OpenMU.Persistence.Initialization.Updates;

/// <summary>Checks fresh data, source-tracked upgrades, customized captions and repeated updates.</summary>
[TestFixture]
[NonParallelizable]
public class MainlandSetNamesTests
{
    /// <summary>Only built-in Chinese set captions are upgraded; existing equipment and options retain their identities.</summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task FreshAndExistingConfigurationsUseMainlandOwnerNamesAsync()
    {
        ConfigurationNameSources.Register();
        var provider = new InMemoryPersistenceContextProvider();
        await new VersionSeasonSix.DataInitialization(provider, NullLoggerFactory.Instance)
            .CreateInitialDataAsync(1, false).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var chinese = CultureInfo.GetCultureInfo("zh-CN");
        var german = CultureInfo.GetCultureInfo("de");
        var expected = new Dictionary<string, string>
        {
            ["Anubis"] = "帕西的传说套装", ["Enis"] = "帕西的强化传说套装",
        };
        var targets = configuration.ItemSetGroups.Where(set => expected.Keys.Any(key => set.Name.SourceKey == $"ItemSetNames/{key}")).ToArray();
        Assert.That(targets, Has.Length.EqualTo(expected.Count));
        foreach (var set in targets)
        {
            var key = set.Name.SourceKey!.Split('/')[1];
            Assert.That(set.Name.GetOwnTranslation(chinese), Is.EqualTo(expected[key]));
            var option = configuration.ItemOptions.Single(option => option.Name.SourceKey == $"ItemOptionNames/{key}AncientSet");
            Assert.That(option.Name.GetOwnTranslation(chinese), Is.EqualTo(expected[key] + "属性"));
            set.Name = set.Name.WithTranslation(chinese, expected[key].Replace("帕西", "帕希", StringComparison.Ordinal)).WithSourceStamp();
            option.Name = option.Name.WithTranslation(chinese, (expected[key] + "属性").Replace("帕西", "帕希", StringComparison.Ordinal)).WithSourceStamp();
        }

        // A missing translation is filled, while an operator's deliberate customization is retained.
        var missing = targets.Single(set => set.Name.SourceKey == "ItemSetNames/Anubis");
        missing.Name = missing.Name.WithTranslation(chinese, null);
        var customized = targets.Single(set => set.Name.SourceKey == "ItemSetNames/Enis");
        customized.Name = customized.Name.WithTranslation(chinese, "管理员自定义魔王套装");
        var customBefore = customized.Name;
        var retained = targets.First();
        retained.Name = retained.Name.WithTranslation(german, "Operator German caption").WithSourceStamp();
        var itemNamesBefore = configuration.Items.Select(item => item.Name).ToArray();
        var identitiesBefore = targets.Select(set => set.GetId()).ToArray();
        var update = new NormalizeMainlandSetNamesPlugIn();
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            foreach (var set in targets.Where(set => set != customized))
            {
                Assert.That(set.Name.GetOwnTranslation(chinese), Is.EqualTo(expected[set.Name.SourceKey!.Split('/')[1]]));
            }

            Assert.That(customized.Name, Is.EqualTo(customBefore));
            Assert.That(retained.Name.GetOwnTranslation(german), Is.EqualTo("Operator German caption"));
            Assert.That(configuration.Items.Select(item => item.Name), Is.EqualTo(itemNamesBefore));
            Assert.That(targets.Select(set => set.GetId()), Is.EqualTo(identitiesBefore));
            Assert.That(configuration.ItemOptions.Where(option => option.Name.SourceKey?.EndsWith("AncientSet", StringComparison.Ordinal) == true)
                .Select(option => option.Name.GetOwnTranslation(chinese)), Has.None.Contains("帕希"));
        });

        var after = targets.Select(set => set.Name).Concat(configuration.ItemOptions.Select(option => option.Name)).ToArray();
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(targets.Select(set => set.Name).Concat(configuration.ItemOptions.Select(option => option.Name)), Is.EqualTo(after));
        Assert.That(ConfigurationCaptions.DetermineChanges(configuration).Where(change => change.CultureName == "zh-CN"
            && expected.Keys.Any(key => change.SourceKey == $"ItemSetNames/{key}")).Count(), Is.EqualTo(1), "Only the preserved customization remains.");
    }
}
