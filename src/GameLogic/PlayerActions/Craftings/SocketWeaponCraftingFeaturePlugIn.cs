// <copyright file="SocketWeaponCraftingFeaturePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.Craftings;

using System.Runtime.InteropServices;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Provides configurable mounted seed sphere values when the socket weapon handler is selected for a recipe.
/// </summary>
[PlugIn]
[Display(Name = nameof(PlugInResources.SocketWeaponCraftingFeaturePlugIn_Name), Description = nameof(PlugInResources.SocketWeaponCraftingFeaturePlugIn_Description), ResourceType = typeof(PlugInResources))]
[Guid("487675a2-84fb-4389-b245-7792ce3f8a83")]
public class SocketWeaponCraftingFeaturePlugIn : IFeaturePlugIn, ISupportCustomConfiguration<SocketWeaponCraftingConfiguration>, ISupportDefaultCustomConfiguration
{
    /// <inheritdoc/>
    public SocketWeaponCraftingConfiguration? Configuration { get; set; }

    /// <inheritdoc/>
    public object CreateDefaultConfig() => new SocketWeaponCraftingConfiguration();
}
