// <copyright file="AddMuExDarkangelStaffUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence.Initialization.CharacterClasses;
using MUnique.OpenMU.Persistence.Initialization.Items;
using MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.Items;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Adds the MuEx Darkangel Staff to a Season 6 item catalog.
/// </summary>
[PlugIn]
[Display(Name = nameof(PlugInResources.AddMuExDarkangelStaff_Name), Description = nameof(PlugInResources.AddMuExDarkangelStaff_Description), ResourceType = typeof(PlugInResources))]
[Guid("35b66573-e5ec-4c5f-9c66-539543b690d2")]
public sealed class AddMuExDarkangelStaffUpdatePlugIn : UpdatePlugInBase
{
    private const byte StaffNumber = 51;

    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override string Name => PlugInResources.AddMuExDarkangelStaff_Name;

    /// <inheritdoc />
    public override string Description => PlugInResources.AddMuExDarkangelStaff_Description;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    public override bool RunsOnNewDatabase => true;

    /// <inheritdoc />
    public override IEnumerable<UpdateDependency> DependsOn => [new(typeof(AddItemStatProfilesUpdatePlugInSeasonSix).GUID)];

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        if (gameConfiguration.Items.Any(item => item.Group == (byte)ItemGroups.Staff && item.Number == StaffNumber))
        {
            return ValueTask.CompletedTask;
        }

        var staff = new Weapons(context, gameConfiguration).CreateMuExDarkangelStaff(StaffNumber);
        var typeId = typeof(WeaponOffhandRestrictionPlugIn).GUID;
        var plugInConfiguration = gameConfiguration.PlugInConfigurations.SingleOrDefault(configuration => configuration.TypeId == typeId);
        if (plugInConfiguration is null)
        {
            plugInConfiguration = context.CreateNew<PlugInConfiguration>();
            plugInConfiguration.SetGuid(typeId);
            plugInConfiguration.TypeId = typeId;
            gameConfiguration.PlugInConfigurations.Add(plugInConfiguration);
        }

        var restrictions = plugInConfiguration.GetConfiguration<Dictionary<Guid, List<byte>>>(new ConfigureNpcTalkPlugInsPlugInBase.IdReferenceHandler()) ?? new();
        restrictions.TryAdd(staff.GetId(), [(byte)CharacterClassNumber.DuelMaster]);
        plugInConfiguration.SetConfiguration(restrictions, null);
        plugInConfiguration.IsActive = true;
        return ValueTask.CompletedTask;
    }
}
