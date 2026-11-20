// <copyright file="MuExDarkLightningPendantDataTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.Persistence.Initialization.Updates;

/// <summary>
/// Verifies the optional MuEx dark pendant definition.
/// </summary>
[TestFixture]
internal class MuExDarkLightningPendantDataTest
{
    /// <summary>
    /// Checks the ordinary pendant, the opt-in definition, and repeat installation.
    /// </summary>
    [Test]
    public async Task AddsAnIndependentDefinitionWithoutShopOrSetMembershipAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new VersionSeasonSix.DataInitialization(provider, NullLoggerFactory.Instance)
            .CreateInitialDataAsync(1, false).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var update = new AddMuExDarkLightningPendantUpdatePlugIn();
        var ordinary = configuration.Items.Single(item => item.Group == 13 && item.Number == 12);
        var packages = configuration.CashShopConfiguration!.Packages.ToArray();
        Assert.That(configuration.Items.Any(IsDarkPendant), Is.False);
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Any(entry => entry.Key == update.Key), Is.False);

        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        var pendant = configuration.Items.Single(IsDarkPendant);
        Assert.That(pendant.GetId(), Is.Not.EqualTo(ordinary.GetId()));
        Assert.That(pendant.Name.GetOwnTranslation(CultureInfo.GetCultureInfo("zh-CN")), Is.EqualTo("雷之项链(黑暗)"));
        Assert.That(pendant.ItemSlot, Is.SameAs(ordinary.ItemSlot));
        Assert.That(pendant.StatProfile, Is.SameAs(ordinary.StatProfile));
        Assert.That((pendant.Width, pendant.Height, pendant.Durability, pendant.DropLevel, pendant.MaximumItemLevel),
            Is.EqualTo((ordinary.Width, ordinary.Height, ordinary.Durability, ordinary.DropLevel, ordinary.MaximumItemLevel)));
        Assert.That(pendant.QualifiedCharacters, Is.EquivalentTo(ordinary.QualifiedCharacters));
        Assert.That(pendant.PossibleItemOptions, Is.EquivalentTo(ordinary.PossibleItemOptions));
        Assert.That(pendant.PossibleItemSetGroups, Is.Empty);
        Assert.That(pendant.DropsFromMonsters || pendant.DropItems.Count != 0, Is.False);
        Assert.That(ordinary.DropsFromMonsters, Is.True);
        Assert.That(configuration.CashShopConfiguration.Packages, Is.EquivalentTo(packages));

        var baseRequirement = ordinary.Requirements.Single();
        var requirement = pendant.Requirements.Single();
        Assert.That(requirement, Is.Not.SameAs(baseRequirement));
        Assert.That((requirement.Attribute, requirement.MinimumValue, requirement.MinimumItemLevel),
            Is.EqualTo((baseRequirement.Attribute, baseRequirement.MinimumValue, baseRequirement.MinimumItemLevel)));
        var basePower = ordinary.BasePowerUpAttributes.Single();
        var power = pendant.BasePowerUpAttributes.Single();
        Assert.That(power, Is.Not.SameAs(basePower));
        Assert.That((power.TargetAttribute, power.BaseValue, power.AggregateType, power.BonusPerLevelTable),
            Is.EqualTo((basePower.TargetAttribute, basePower.BaseValue, basePower.AggregateType, basePower.BonusPerLevelTable)));

        pendant.Durability = 32;
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(configuration.Items.Single(IsDarkPendant), Is.SameAs(pendant));
        Assert.That(ordinary.Durability, Is.Not.EqualTo(pendant.Durability));
        Assert.That(pendant.Durability, Is.EqualTo(32));
    }

    /// <summary>
    /// An installation with a custom definition keeps that definition.
    /// </summary>
    [Test]
    public async Task KeepsACustomDefinitionEvenWithoutAnOrdinaryPendantAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        using var context = provider.CreateNewContext();
        var configuration = context.CreateNew<GameConfiguration>();
        var custom = context.CreateNew<ItemDefinition>();
        custom.Group = 13;
        custom.Number = 401;
        custom.Durability = 88;
        configuration.Items.Add(custom);

        await new AddMuExDarkLightningPendantUpdatePlugIn().ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(configuration.Items.Single(), Is.SameAs(custom));
        Assert.That(custom.Durability, Is.EqualTo(88));
    }

    private static bool IsDarkPendant(ItemDefinition item) => item.Group == 13 && item.Number == 401;
}
