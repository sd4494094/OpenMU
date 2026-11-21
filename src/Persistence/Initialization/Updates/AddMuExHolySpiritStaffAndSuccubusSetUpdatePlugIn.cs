// <copyright file="AddMuExHolySpiritStaffAndSuccubusSetUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence.Initialization.Items;
using MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.Items;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Adds opt-in server definitions for the MuEx Holy Spirit Staff and Succubus armor set.
/// </summary>
[PlugIn]
[Display(Name = nameof(PlugInResources.AddMuExHolySpiritStaffAndSuccubusSet_Name), Description = nameof(PlugInResources.AddMuExHolySpiritStaffAndSuccubusSet_Description), ResourceType = typeof(PlugInResources))]
[Guid("f437d9b8-4098-4b19-9910-ff734557ea27")]
public sealed class AddMuExHolySpiritStaffAndSuccubusSetUpdatePlugIn : UpdatePlugInBase
{
    private const byte StaffGroup = 5;
    private const byte StaffNumber = 32;
    private const byte SuccubusNumber = 53;
    private const string StaffProfileName = "muExHolySpiritStaff";

    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override string Name => PlugInResources.AddMuExHolySpiritStaffAndSuccubusSet_Name;

    /// <inheritdoc />
    public override string Description => PlugInResources.AddMuExHolySpiritStaffAndSuccubusSet_Description;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override DateTime UpdatedAt => new(2026, 11, 21, 9, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override int Version => 2;

    /// <inheritdoc />
    public override IEnumerable<UpdateDependency> DependsOn => [new(typeof(AddItemStatProfilesUpdatePlugInSeasonSix).GUID)];

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    public override bool RunsOnNewDatabase => true;

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
        => this.ApplyAsync(context, gameConfiguration, null);

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration, int? installedVersion)
    {
        var staff = gameConfiguration.Items.FirstOrDefault(item => item.Group == StaffGroup && item.Number == StaffNumber);
        if (staff is null)
        {
            var weapons = new Weapons(context, gameConfiguration);
            weapons.CreateMuExHolySpiritStaff(StaffNumber).StatProfile = GetStaffProfile(context, gameConfiguration);
        }
        else if (installedVersion == 1 && IsUnprofiledImport(staff, nameof(ItemNames.MuExHolySpiritStaff)))
        {
            staff.StatProfile = GetStaffProfile(context, gameConfiguration);
        }

        var armors = new Armors(context, gameConfiguration);
        (byte Group, string NameResource)[] pieces =
        [
            (7, nameof(ItemNames.MuExSuccubusHelm)),
            (8, nameof(ItemNames.MuExSuccubusArmor)),
            (9, nameof(ItemNames.MuExSuccubusPants)),
            (10, nameof(ItemNames.MuExSuccubusGloves)),
            (11, nameof(ItemNames.MuExSuccubusBoots)),
        ];
        foreach (var (group, nameResource) in pieces)
        {
            var piece = gameConfiguration.Items.FirstOrDefault(item => item.Group == group && item.Number == SuccubusNumber);
            if (piece is null)
            {
                piece = armors.CreateMuExSuccubusPiece(group);
            }
            else if (installedVersion != 1 || !IsUnprofiledImport(piece, nameResource))
            {
                continue;
            }

            piece.StatProfile = gameConfiguration.ItemStatProfiles.Single(profile => profile.Name == BuiltInItemStatProfiles.Armor);
        }

        return ValueTask.CompletedTask;
    }

    private static bool IsUnprofiledImport(ItemDefinition item, string nameResource)
        => item.StatProfile is null
           && item.GetId() == GuidHelper.CreateGuid<ItemDefinition>(item.Group, item.Number)
           && item.Name.SourceKey == $"ItemNames/{nameResource}";

    private static ItemStatProfile GetStaffProfile(IContext context, GameConfiguration gameConfiguration)
    {
        if (gameConfiguration.ItemStatProfiles.SingleOrDefault(profile => profile.Name == StaffProfileName) is { } existing)
        {
            return existing;
        }

        var profile = context.CreateNew<ItemStatProfile>();
        profile.Name = StaffProfileName;
        // MuMain's weapon profile requires 5 strength per additional option level.
        profile.StrengthPerOptionLevel = 5;
        gameConfiguration.ItemStatProfiles.Add(profile);
        return profile;
    }
}
