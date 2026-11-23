// <copyright file="InventoryItemEquipFeaturePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.Items;

using System.Runtime.InteropServices;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Enables MuMain inventory state markers for explicitly configured items.
/// </summary>
[PlugIn]
[Guid("56848883-633e-4b7c-b563-a95596557aee")]
[Display(Name = nameof(PlugInResources.InventoryItemEquipFeature_Name), Description = nameof(PlugInResources.InventoryItemEquipFeature_Description), ResourceType = typeof(PlugInResources))]
public class InventoryItemEquipFeaturePlugIn : IFeaturePlugIn, IDisabledByDefault, ISupportCustomConfiguration<Dictionary<byte, List<short>>>, ISupportDefaultCustomConfiguration
{
    /// <summary>
    /// Gets or sets the eligible item numbers by item group.
    /// </summary>
    public Dictionary<byte, List<short>>? Configuration { get; set; }

    /// <inheritdoc />
    public object CreateDefaultConfig() => new Dictionary<byte, List<short>>();
}
