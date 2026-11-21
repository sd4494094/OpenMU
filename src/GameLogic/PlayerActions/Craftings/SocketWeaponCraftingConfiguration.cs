// <copyright file="SocketWeaponCraftingConfiguration.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.Craftings;

/// <summary>
/// Material pricing for the level 400 socket weapon recipes.
/// </summary>
public class SocketWeaponCraftingConfiguration
{
    private static readonly int[] DefaultSeedSphereValues = [1_050_000, 1_125_000, 1_200_000, 1_275_000, 1_350_000];

    /// <summary>
    /// Gets or sets the material values of mounted seed spheres for tiers 1 to 5, in order.
    /// </summary>
    /// <remarks>
    /// The default values are from the MuEx item catalog, where all six elements at a given tier have the same price.
    /// Each value is added to the equipment's material value before rounding. A zero disables the extra value for that tier.
    /// </remarks>
    [Display(ResourceType = typeof(PlugInResources), Name = nameof(PlugInResources.SocketWeaponCraftingConfiguration_MountedSeedSphereValues_Name))]
    public IList<int> MountedSeedSphereValues { get; set; } = new List<int>(DefaultSeedSphereValues);

    /// <summary>
    /// Gets the material value for the tier sent to the client, falling back to the default for an omitted configuration entry.
    /// </summary>
    /// <param name="configuration">The optional custom values.</param>
    /// <param name="tier">The one-based sphere tier.</param>
    /// <returns>The nonnegative material value.</returns>
    internal static int GetMountedSeedSphereValue(SocketWeaponCraftingConfiguration? configuration, int tier)
    {
        // The item serializer clamps legacy values to the same five tiers.
        var index = Math.Clamp(tier, 1, DefaultSeedSphereValues.Length) - 1;
        return configuration?.MountedSeedSphereValues is { } values && index < values.Count
            ? Math.Max(0, values[index])
            : DefaultSeedSphereValues[index];
    }
}
