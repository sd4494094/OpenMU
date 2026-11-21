// <copyright file="MuExHolySpiritStaffAndSuccubusSetDataTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.Persistence.Initialization.CharacterClasses;
using MUnique.OpenMU.Persistence.Initialization.Updates;

/// <summary>
/// Verifies opt-in definitions for the MuEx Holy Spirit Staff and Succubus armor set.
/// </summary>
[TestFixture]
internal class MuExHolySpiritStaffAndSuccubusSetDataTest
{
    /// <summary>
    /// Checks installation, localized names, catalog fields, idempotence, and custom item preservation.
    /// </summary>
    /// <param name="preserveCustomDefinition">Whether one of the item IDs is already configured.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async Task AddsMissingDefinitionsWithoutChangingDropsOrRecipesAsync(bool preserveCustomDefinition)
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new VersionSeasonSix.DataInitialization(provider, NullLoggerFactory.Instance)
            .CreateInitialDataAsync(1, true).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var update = new AddMuExHolySpiritStaffAndSuccubusSetUpdatePlugIn();
        Assert.That(update.IsMandatory, Is.False);
        Assert.That(update.RunsOnNewDatabase, Is.True);
        Assert.That(configuration.Items.Where(IsTargetItem), Is.Empty);
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Any(entry => entry.Key == update.Key), Is.False);

        // A database can return the slot accepting either hand before the main-hand-only slot.
        var slotTypes = configuration.ItemSlotTypes.OrderByDescending(type => type.ItemSlots.Count).ToArray();
        configuration.ItemSlotTypes.Clear();
        foreach (var slotType in slotTypes)
        {
            configuration.ItemSlotTypes.Add(slotType);
        }

        ItemDefinition? custom = null;
        if (preserveCustomDefinition)
        {
            custom = context.CreateNew<ItemDefinition>();
            custom.Group = 10;
            custom.Number = 53;
            custom.Name = new LocalizedString("Custom Succubus gloves");
            custom.Width = 2;
            custom.Durability = 7;
            custom.DropsFromMonsters = false;
            configuration.Items.Add(custom);
        }

        var recipes = configuration.Monsters.SelectMany(monster => monster.ItemCraftings).ToArray();
        var tables = configuration.ItemLevelBonusTables.ToArray();
        var itemCountBefore = configuration.Items.Count;
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);

        var items = configuration.Items.Where(IsTargetItem).OrderBy(item => item.Group).ToArray();
        Assert.That(items.Select(item => (item.Group, item.Number)), Is.EqualTo(new[]
        {
            ((byte)5, (short)32),
            ((byte)7, (short)53),
            ((byte)8, (short)53),
            ((byte)9, (short)53),
            ((byte)10, (short)53),
            ((byte)11, (short)53),
        }));
        Assert.That(configuration.Items.Count, Is.EqualTo(itemCountBefore + (preserveCustomDefinition ? 5 : 6)));
        Assert.That(configuration.Monsters.SelectMany(monster => monster.ItemCraftings), Is.EquivalentTo(recipes));
        Assert.That(configuration.ItemLevelBonusTables, Is.EquivalentTo(tables));

        var chinese = CultureInfo.GetCultureInfo("zh-CN");
        var importedNames = new Dictionary<(byte Group, short Number), string>
        {
            [(5, 32)] = "圣灵之杖",
            [(7, 53)] = "女妖之盔",
            [(8, 53)] = "女妖之铠",
            [(9, 53)] = "女妖之护腿",
            [(10, 53)] = "女妖之护手",
            [(11, 53)] = "女妖之靴",
        };
        foreach (var item in items.Where(item => !ReferenceEquals(item, custom)))
        {
            Assert.That(item.Name.GetOwnTranslation(chinese), Is.EqualTo(importedNames[(item.Group, item.Number)]));
        }
        Assert.That(items.Select(item => item.DropsFromMonsters || item.DropItems.Count != 0), Is.All.False);
        Assert.That(items.Select(item => item.PossibleItemSetGroups.Count), Is.All.Zero);

        var importedItems = items.Where(item => !ReferenceEquals(item, custom)).ToArray();
        foreach (var item in importedItems)
        {
            Assert.That(item.ItemSlot!.ItemSlots, Is.EqualTo(new[] { item.Group - 5 }));
            Assert.That(item.QualifiedCharacters.Select(character => character.Number), Is.EqualTo(new[] { (byte)CharacterClassNumber.DimensionMaster }));
            AssertAdditionalStrength(context, configuration, item);
        }

        var expectedItemValues = new[]
        {
            ((byte)1, (byte)4, (byte)130, (byte)149),
            ((byte)2, (byte)2, (byte)90, (byte)143),
            ((byte)2, (byte)2, (byte)90, (byte)148),
            ((byte)2, (byte)2, (byte)90, (byte)145),
            ((byte)2, (byte)2, (byte)90, (byte)142),
            ((byte)2, (byte)2, (byte)90, (byte)140),
        };
        var expectedImportedValues = preserveCustomDefinition
            ? expectedItemValues.Where((_, index) => index != 4).ToArray()
            : expectedItemValues;
        Assert.That(importedItems.Select(item => (item.Width, item.Height, item.Durability, item.DropLevel)), Is.EqualTo(expectedImportedValues));

        var staff = items[0];
        var staffPowers = staff.BasePowerUpAttributes.ToDictionary(power => power.TargetAttribute!);
        Assert.That((staffPowers[Stats.MinimumPhysBaseDmgByWeapon].BaseValue,
            staffPowers[Stats.MaximumPhysBaseDmgByWeapon].BaseValue,
            staffPowers[Stats.AttackSpeedByWeapon].BaseValue,
            staffPowers[Stats.StaffRise].BaseValue), Is.EqualTo((59f, 61f, 30f, 55f)));
        var succubusPieces = importedItems.Skip(1).ToArray();
        foreach (var item in succubusPieces)
        {
            var requirements = item.Requirements.ToDictionary(requirement => requirement.Attribute!, requirement => requirement.MinimumValue);
            Assert.That((requirements[Stats.Level], requirements[Stats.TotalStrengthRequirementValue],
                requirements[Stats.TotalAgilityRequirementValue], requirements[Stats.TotalEnergyRequirementValue]),
                Is.EqualTo((400f, 56f, 19f, 157f)));
        }

        var gloves = items.Single(item => item.Group == 10);
        if (custom is null)
        {
            Assert.That(gloves.BasePowerUpAttributes.Single(power => power.TargetAttribute == Stats.AttackSpeedAny).BaseValue, Is.EqualTo(6));
        }
        else
        {
            Assert.That(gloves, Is.SameAs(custom));
            Assert.That((gloves.Name.ValueInNeutralLanguage, gloves.Width, gloves.Durability), Is.EqualTo(("Custom Succubus gloves", (byte)2, (byte)7)));
        }

        var installedStaff = staff;
        installedStaff.Durability = 77;
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(configuration.Items.Where(IsTargetItem), Is.EquivalentTo(items));
        Assert.That(installedStaff.Durability, Is.EqualTo(77));
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Count(entry => entry.Key == update.Key), Is.EqualTo(1));
    }

    /// <summary>
    /// Upgrades version 1 items without replacing custom definitions or assigned profiles.
    /// </summary>
    [Test]
    public async Task UpdatesOnlyUnprofiledImportedEquipmentFromVersionOneAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new VersionSeasonSix.DataInitialization(provider, NullLoggerFactory.Instance)
            .CreateInitialDataAsync(1, false).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var update = new AddMuExHolySpiritStaffAndSuccubusSetUpdatePlugIn();
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);

        var imported = configuration.Items.Where(IsTargetItem).ToArray();
        var originalProfiles = imported.Select(item => item.StatProfile).ToArray();
        foreach (var item in imported)
        {
            item.StatProfile = null;
        }

        var staff = imported.Single(item => item.Group == 5);
        staff.Durability = 77;
        var boots = imported.Single(item => item.Group == 11);
        var customProfile = context.CreateNew<ItemStatProfile>();
        customProfile.Name = "customBoots";
        configuration.ItemStatProfiles.Add(customProfile);
        boots.StatProfile = customProfile;
        var gloves = imported.Single(item => item.Group == 10);
        gloves.Name = new LocalizedString("Custom gloves");
        var entry = (await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Single(current => current.Key == update.Key);
        entry.Version = 1;

        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(entry.Version, Is.EqualTo(2));
        Assert.That(configuration.Items.Where(IsTargetItem), Is.EquivalentTo(imported));
        Assert.That(staff.Durability, Is.EqualTo(77));
        Assert.That(boots.StatProfile, Is.SameAs(customProfile));
        Assert.That(gloves.StatProfile, Is.Null);
        Assert.That(imported.Except([boots, gloves]).Select(item => item.StatProfile),
            Is.EqualTo(imported.Select((item, index) => (item, index)).Where(pair => pair.item != boots && pair.item != gloves).Select(pair => originalProfiles[pair.index])));
        AssertAdditionalStrength(context, configuration, staff);
        AssertAdditionalStrength(context, configuration, imported.Single(item => item.Group == 7));
    }

    private static void AssertAdditionalStrength(IContext context, GameConfiguration configuration, ItemDefinition definition)
    {
        var item = context.CreateNew<Item>();
        item.Definition = definition;
        item.Level = 15;
        var baseStrength = item.GetRequirements(configuration).Single(requirement => requirement.Attr == Stats.TotalStrength).Value;
        var option = context.CreateNew<ItemOptionLink>();
        option.ItemOption = definition.PossibleItemOptions.SelectMany(set => set.PossibleOptions).First(entry => entry.OptionType == ItemOptionTypes.Option);
        option.Level = 4;
        item.ItemOptions.Add(option);
        var withOption = item.GetRequirements(configuration).Single(requirement => requirement.Attr == Stats.TotalStrength).Value;
        Assert.That(withOption - baseStrength, Is.EqualTo(20));
    }

    private static bool IsTargetItem(ItemDefinition item) =>
        (item.Group == 5 && item.Number == 32) ||
        (item.Group is >= 7 and <= 11 && item.Number == 53);
}
