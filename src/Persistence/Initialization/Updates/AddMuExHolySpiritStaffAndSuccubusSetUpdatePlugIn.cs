// <copyright file="AddMuExHolySpiritStaffAndSuccubusSetUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Interfaces;
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

    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override string Name => PlugInResources.AddMuExHolySpiritStaffAndSuccubusSet_Name;

    /// <inheritdoc />
    public override string Description => PlugInResources.AddMuExHolySpiritStaffAndSuccubusSet_Description;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    public override bool RunsOnNewDatabase => true;

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        if (!gameConfiguration.Items.Any(item => item.Group == StaffGroup && item.Number == StaffNumber))
        {
            var weapons = new Weapons(context, gameConfiguration);
            weapons.CreateMuExHolySpiritStaff(StaffNumber);
        }

        var armors = new Armors(context, gameConfiguration);
        foreach (var itemGroup in new byte[] { 7, 8, 9, 10, 11 })
        {
            if (gameConfiguration.Items.Any(item => item.Group == itemGroup && item.Number == SuccubusNumber))
            {
                continue;
            }

            armors.CreateMuExSuccubusPiece(itemGroup);
        }

        return ValueTask.CompletedTask;
    }
}
