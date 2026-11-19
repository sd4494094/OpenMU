// <copyright file="AcheronEntryItems.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.Items;

using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.Interfaces;

/// <summary>
/// Initializes the item definitions for entry to Acheron.
/// </summary>
internal sealed class AcheronEntryItems(IContext context, GameConfiguration gameConfiguration) : InitializerBase(context, gameConfiguration)
{
    /// <inheritdoc />
    public override void Initialize()
    {
        this.AddIfMissing(145, ItemNames.ResourceManager.GetLocalizedString("SpiritMapFragment"), 5);
        this.AddIfMissing(146, ItemNames.ResourceManager.GetLocalizedString("SpiritMap"), 1);
    }

    private void AddIfMissing(short number, LocalizedString name, byte stackLimit)
    {
        const byte group = 13;
        if (this.GameConfiguration.Items.Any(item => item.Group == group && item.Number == number))
        {
            return;
        }

        var item = this.Context.CreateNew<ItemDefinition>();
        item.Group = group;
        item.Number = number;
        item.Name = name;
        item.Width = 1;
        item.Height = 1;
        item.Durability = stackLimit;
        item.DropsFromMonsters = false;
        item.SetGuid(group, number);
        this.GameConfiguration.Items.Add(item);
    }
}
