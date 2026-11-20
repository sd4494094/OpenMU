// <copyright file="AddMuExCommissionItemsUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Adds the MuEx level 2 commission item definitions to a Season 6 configuration.
/// </summary>
[PlugIn]
[Display(Name = nameof(PlugInResources.AddMuExCommissionItems_Name), Description = nameof(PlugInResources.AddMuExCommissionItems_Description), ResourceType = typeof(PlugInResources))]
[Guid("769e336c-483e-4d85-a0f5-577ef6395a93")]
public sealed class AddMuExCommissionItemsUpdatePlugIn : UpdatePlugInBase
{
    private const byte CommissionItemGroup = 14;
    private const short CommissionLetterNumber = 170;
    private const short CommissionCompletionScrollNumber = 171;

    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override string Name => PlugInResources.AddMuExCommissionItems_Name;

    /// <inheritdoc />
    public override string Description => PlugInResources.AddMuExCommissionItems_Description;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    public override bool RunsOnNewDatabase => true;

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        AddItemIfMissing(context, gameConfiguration, CommissionLetterNumber, nameof(ItemNames.MuExLevelTwoCommissionLetter));
        AddItemIfMissing(context, gameConfiguration, CommissionCompletionScrollNumber, nameof(ItemNames.MuExLevelTwoCommissionCompletionScroll));
        return ValueTask.CompletedTask;
    }

    private static void AddItemIfMissing(IContext context, GameConfiguration configuration, short number, string nameKey)
    {
        if (configuration.Items.Any(item => item.Group == CommissionItemGroup && item.Number == number))
        {
            return;
        }

        var item = context.CreateNew<ItemDefinition>();
        item.Group = CommissionItemGroup;
        item.Number = number;
        item.Name = ItemNames.ResourceManager.GetLocalizedString(nameKey);
        item.Width = 1;
        item.Height = 2;
        item.IsRepairable = false;

        // Use one point of durability for test inventory items until the original behavior is verified.
        item.Durability = 1;
        item.SetGuid(CommissionItemGroup, number);
        configuration.Items.Add(item);
    }
}
