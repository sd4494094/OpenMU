// <copyright file="HistoricalEquipmentNamesTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.Mcp;

using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.Persistence.Initialization;
using MUnique.OpenMU.Web.AdminPanel.Operations;

/// <summary>Verifies old client names resolve to canonical equipment without introducing collisions or invented items.</summary>
[TestFixture]
public class HistoricalEquipmentNamesTests
{
    /// <summary>The mainland item dictionary and summoner page identify these aliases independently of the alias resource.</summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task OldNamesResolveToTheCorrectGameNumbersAsync()
    {
        ConfigurationNameSources.Register();
        var persistence = new InMemoryPersistenceContextProvider();
        await new MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.DataInitialization(persistence, NullLoggerFactory.Instance)
            .CreateInitialDataAsync(1, false).ConfigureAwait(false);
        using var context = persistence.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var examples = new[]
        {
            (Old: "弗拉姆伯格", Name: "虚伪之剑", Group: 0, Number: 26),
            (Old: "费拉姆伯格", Name: "虚伪之剑", Group: 0, Number: 26),
            (Old: "符文巨剑", Name: "双子之剑", Group: 0, Number: 28),
            (Old: "黑暗毒刺", Name: "傲视魔弓", Group: 4, Number: 23),
            (Old: "撒姆特之书", Name: "火龙兽之书", Group: 5, Number: 21),
            (Old: "信仰头盔", Name: "灵光之盔", Group: 7, Number: 50),
        };
        foreach (var example in examples)
        {
            var found = configuration.Items.Where(item => GameplayContentNames.Matches(GameplayContentNames.Resolve("ItemNames", item.Name), example.Old, $"{item.Group}/{item.Number}")).ToArray();
            Assert.That(found, Has.Length.EqualTo(1), example.Old);
            Assert.Multiple(() =>
            {
                Assert.That((found[0].Group, found[0].Number), Is.EqualTo(((byte)example.Group, (short)example.Number)));
                Assert.That(GameplayContentNames.Display(GameplayContentNames.Resolve("ItemNames", found[0].Name)), Is.EqualTo(example.Name));
            });
        }

        // The existing search is partial: this old name also occurs within the official
        // Storm Hard name, so it must remain an explicit candidate list.
        var ambiguous = configuration.Items.Where(item => GameplayContentNames.Matches(
            GameplayContentNames.Resolve("ItemNames", item.Name), "毁灭之铠", $"{item.Group}/{item.Number}")).ToArray();
        Assert.That(ambiguous.Select(item => (item.Group, item.Number)), Is.EquivalentTo(new[] { ((byte)8, (short)48), ((byte)8, (short)60) }));

        foreach (var example in new[] { (Name: "破灭之铠", Number: 48), (Name: "暴风扎哈德之盔", Number: 72) })
        {
            var found = configuration.Items.Where(item => GameplayContentNames.Matches(GameplayContentNames.Resolve("ItemNames", item.Name), example.Name, $"{item.Group}/{item.Number}")).ToArray();
            Assert.That(found, Has.Length.EqualTo(1), "A canonical name must not become another item's old alias.");
            Assert.That(found[0].Number, Is.EqualTo(example.Number));
        }

        Assert.That(configuration.Items.Any(item => GameplayContentNames.Matches(GameplayContentNames.Resolve("ItemNames", item.Name), "神圣风暴之剑", $"{item.Group}/{item.Number}")), Is.False);
        var legacy = new LocalizedString("Flamberge");
        var resolved = GameplayContentNames.Resolve("ItemNames", legacy);
        Assert.That(GameplayContentNames.Aliases(resolved), Does.Contain("弗拉姆伯格"));
        Assert.That(legacy.SourceKey, Is.Null, "Resolution must not mutate persisted names.");
        Assert.That(GameplayContentNames.Aliases(GameplayContentNames.Resolve("MapNames", legacy)), Does.Not.Contain("弗拉姆伯格"));
        var customized = resolved.WithTranslation(CultureInfo.GetCultureInfo("zh-CN"), "自定义剑名");
        Assert.That(GameplayContentNames.Display(GameplayContentNames.Resolve("ItemNames", customized)), Is.EqualTo("自定义剑名"));
    }
}
