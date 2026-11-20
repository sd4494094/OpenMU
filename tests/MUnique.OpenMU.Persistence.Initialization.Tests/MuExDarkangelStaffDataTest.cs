// <copyright file="MuExDarkangelStaffDataTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.Persistence.Initialization.CharacterClasses;
using MUnique.OpenMU.Persistence.Initialization.Updates;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Verifies the original catalog fields and opt-in behavior of the Darkangel Staff.
/// </summary>
[TestFixture]
internal class MuExDarkangelStaffDataTest
{
    /// <summary>
    /// Checks existing and fresh configuration, staff rules, and repeat installation.
    /// </summary>
    [Test]
    public async Task AddsTheStaffWithItsOwnOffhandRestrictionAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new VersionSeasonSix.DataInitialization(provider, NullLoggerFactory.Instance)
            .CreateInitialDataAsync(1, false).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var update = new AddMuExDarkangelStaffUpdatePlugIn();
        var typeId = typeof(WeaponOffhandRestrictionPlugIn).GUID;
        var restrictions = configuration.PlugInConfigurations.SingleOrDefault(entry => entry.TypeId == typeId);
        var packages = configuration.CashShopConfiguration!.Packages.ToArray();
        var otherItem = configuration.Items.First().GetId();

        Assert.That(update.RunsOnNewDatabase, Is.True);
        Assert.That(configuration.Items.Any(IsDarkangelStaff), Is.False);
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Any(entry => entry.Key == update.Key), Is.False);

        if (restrictions is null)
        {
            restrictions = context.CreateNew<PlugInConfiguration>();
            restrictions.TypeId = typeId;
            configuration.PlugInConfigurations.Add(restrictions);
        }

        restrictions.SetConfiguration(new Dictionary<Guid, List<byte>> { [otherItem] = [(byte)CharacterClassNumber.BladeMaster] }, null);
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);

        var staff = configuration.Items.Single(IsDarkangelStaff);
        Assert.That(staff.Name.GetOwnTranslation(CultureInfo.GetCultureInfo("zh-CN")), Is.EqualTo("黑暗天使魔杖"));
        Assert.That((staff.Width, staff.Height, staff.Durability, staff.DropLevel, staff.MaximumItemLevel),
            Is.EqualTo((1, 4, 100, 160, 15)));
        Assert.That(staff.DropsFromMonsters || staff.DropItems.Count != 0, Is.False);
        Assert.That(staff.Skill, Is.Null);
        Assert.That(staff.PossibleItemSetGroups, Is.Empty);
        Assert.That(staff.ItemSlot!.ItemSlots, Is.EquivalentTo(new[] { (int)InventoryConstants.LeftHandSlot, InventoryConstants.RightHandSlot }));
        Assert.That(staff.QualifiedCharacters.Select(character => character.Number),
            Is.EquivalentTo(new[] { (byte)CharacterClassNumber.GrandMaster, (byte)CharacterClassNumber.DuelMaster }));
        Assert.That(staff.StatProfile!.Name, Is.EqualTo("muExDarkangelStaff"));
        Assert.That(configuration.CashShopConfiguration.Packages, Is.EquivalentTo(packages));

        var powers = staff.BasePowerUpAttributes.ToDictionary(power => power.TargetAttribute!);
        Assert.That(powers[Stats.MinimumPhysBaseDmgByWeapon].BaseValue, Is.EqualTo(120));
        Assert.That(powers[Stats.MaximumPhysBaseDmgByWeapon].BaseValue, Is.EqualTo(126));
        Assert.That(powers[Stats.AttackSpeedByWeapon].BaseValue, Is.EqualTo(30));
        Assert.That(powers[Stats.StaffRise].BaseValue, Is.EqualTo(86));
        Assert.That(powers[Stats.StaffRise].BonusPerLevelTable!.BonusPerLevel.Single(bonus => bonus.Level == 15).AdditionalValue, Is.EqualTo(63));
        Assert.That(powers[Stats.MinimumPhysBaseDmgByWeapon].BonusPerLevelTable!.BonusPerLevel.Single(bonus => bonus.Level == 15).AdditionalValue, Is.EqualTo(66));
        Assert.That(staff.PossibleItemOptions.SelectMany(definition => definition.PossibleOptions)
            .Where(option => option.OptionType == ItemOptionTypes.GuardianOption)
            .Select(option => (option.PowerUpDefinition!.TargetAttribute!, option.PowerUpDefinition.Boost!.ConstantValue.Value)),
            Is.EquivalentTo(new[] { (Stats.AttackRatePvp, 10f), (Stats.FinalDamageIncreasePvp, 200f) }));

        var item = context.CreateNew<Item>();
        item.Definition = staff;
        var requirements = item.GetRequirements(configuration).ToDictionary(requirement => requirement.Attr, requirement => requirement.Value);
        Assert.That((requirements[Stats.Level], requirements[Stats.TotalStrength], requirements[Stats.TotalAgility]), Is.EqualTo((400, 245, 87)));
        item.Level = 15;
        requirements = item.GetRequirements(configuration).ToDictionary(requirement => requirement.Attr, requirement => requirement.Value);
        Assert.That((requirements[Stats.Level], requirements[Stats.TotalStrength], requirements[Stats.TotalAgility]), Is.EqualTo((400, 309, 106)));

        Assert.That(restrictions.IsActive, Is.True);
        var rules = restrictions.GetConfiguration<Dictionary<Guid, List<byte>>>(null)!;
        Assert.That(rules[staff.GetId()], Is.EqualTo(new[] { (byte)CharacterClassNumber.DuelMaster }));
        Assert.That(rules[otherItem], Is.EqualTo(new[] { (byte)CharacterClassNumber.BladeMaster }));
        staff.Durability = 91;
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(configuration.Items.Single(IsDarkangelStaff), Is.SameAs(staff));
        Assert.That(staff.Durability, Is.EqualTo(91));
    }

    /// <summary>
    /// Checks that a definition created by the server owner keeps its properties.
    /// </summary>
    [Test]
    public async Task PreservesExistingCustomDefinitionAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        using var context = provider.CreateNewContext();
        var configuration = context.CreateNew<GameConfiguration>();
        var custom = context.CreateNew<ItemDefinition>();
        custom.Group = 5;
        custom.Number = 51;
        custom.Durability = 81;
        configuration.Items.Add(custom);

        await new AddMuExDarkangelStaffUpdatePlugIn().ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(configuration.Items.Single(), Is.SameAs(custom));
        Assert.That(custom.Durability, Is.EqualTo(81));
        Assert.That(configuration.PlugInConfigurations, Is.Empty);
    }

    private static bool IsDarkangelStaff(ItemDefinition item) => item.Group == 5 && item.Number == 51;
}
