// <copyright file="DropItemEligibility.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic;

using MUnique.OpenMU.DataModel.Configuration.Items;

/// <summary>Shared item-level eligibility used by actual drops and read-only source explanations.</summary>
public static class DropItemEligibility
{
    /// <summary>Checks the configured minimum and optional maximum monster drop level.</summary>
    /// <param name="item">The candidate item.</param>
    /// <param name="monsterLevel">The level used for selection.</param>
    /// <returns>Whether the configured level interval contains the monster level.</returns>
    public static bool IsWithinDropLevels(ItemDefinition item, int monsterLevel)
        => item.DropLevel <= monsterLevel && (item.MaximumDropLevel is not { } maximum || monsterLevel <= maximum);

    /// <summary>Checks the ordinary twelve-level pool and socket classification.</summary>
    /// <param name="item">The candidate item.</param>
    /// <param name="monsterLevel">The level used for selection.</param>
    /// <param name="socketItem">Whether the socket pool is selected.</param>
    /// <returns>Whether the item belongs to this random drop pool.</returns>
    public static bool IsInRandomPool(ItemDefinition item, int monsterLevel, bool socketItem)
        => monsterLevel is >= byte.MinValue and <= byte.MaxValue
            && IsWithinDropLevels(item, monsterLevel)
            && item.DropLevel > monsterLevel - 12
            && (item.MaximumSockets > 0) == socketItem;

    /// <summary>Checks an explicitly listed map or quest item, including the jewel exception.</summary>
    /// <param name="item">The candidate item.</param>
    /// <param name="monsterLevel">The monster level.</param>
    /// <param name="jewel">Whether the group uses jewel selection.</param>
    /// <returns>Whether the item passes the shared listed-item filter.</returns>
    public static bool IsInListedPool(ItemDefinition item, int monsterLevel, bool jewel)
        => IsWithinDropLevels(item, monsterLevel)
            && (jewel || item.DropLevel == 0 || item.DropLevel > monsterLevel - 12);
}
