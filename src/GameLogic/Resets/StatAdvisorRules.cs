// <copyright file="StatAdvisorRules.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Resets;

using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.Interfaces;

/// <summary>Read-only validation and quotes for the advisor.</summary>
internal static class StatAdvisorRules
{
    // The character information/level-up path still contains 16-bit base stat fields.
    // This bounds the new operation, without silently changing the legacy stat protocol.

    /// <summary>The maximum base-stat value supported by the legacy level-up packet.</summary>
    internal const int MaximumSupportedStat = ushort.MaxValue;

    /// <summary>The stable base-attribute indices used by the advisor protocol.</summary>
    internal static readonly AttributeDefinition[] Attributes =
        [Stats.BaseStrength, Stats.BaseAgility, Stats.BaseVitality, Stats.BaseEnergy, Stats.BaseLeadership];

    private static readonly string[] LabelKeys = ["AdvisorStrength", "AdvisorAgility", "AdvisorVitality", "AdvisorEnergy", "AdvisorLeadership"];

    /// <summary>Gets the attributes which the player's class allows them to increase.</summary>
    /// <param name="player">The player whose active character is being inspected.</param>
    /// <param name="amounts">The current draft, if any.</param>
    /// <returns>The supported attributes and their server values.</returns>
    internal static IReadOnlyList<StatAdvisorAttribute> GetStats(Player player, int[]? amounts = null)
    {
        var result = new List<StatAdvisorAttribute>();
        for (var i = 0; i < Attributes.Length; i++)
        {
            var stat = player.SelectedCharacter?.CharacterClass?.GetStatAttribute(Attributes[i]);
            if (stat is not { IncreasableByPlayer: true, Attribute: { } attribute } || player.Attributes is null)
            {
                continue;
            }

            var maximum = (int)Math.Min(MaximumSupportedStat, attribute.MaximumValue ?? MaximumSupportedStat);
            result.Add(new(i, player.GetLocalizedMessage(LabelKeys[i]), (int)player.Attributes[attribute], maximum, amounts?[i] ?? 0));
        }

        return result;
    }

    /// <summary>Validates the complete allocation without changing character data.</summary>
    /// <param name="player">The player whose points and class are checked.</param>
    /// <param name="amounts">The proposed amounts for the stable attribute indices.</param>
    /// <returns>An empty string when valid, otherwise a localized failure.</returns>
    internal static string AllocationFailure(Player player, int[] amounts)
    {
        if (player.CurrentMiniGame is not null)
        {
            return player.GetLocalizedMessage(nameof(PlayerMessage.AddingMultiplePointsWhileMiniGameNotAllowed));
        }

        var stats = GetStats(player);
        long sum = 0;
        for (var i = 0; i < amounts.Length; i++)
        {
            var row = stats.FirstOrDefault(s => s.Id == i);
            if (amounts[i] < 0 || (amounts[i] != 0 && (row is null || (long)row.Current + amounts[i] > row.Maximum)))
            {
                return player.GetLocalizedMessage("AdvisorInvalidAllocation");
            }

            sum += amounts[i];
        }

        return sum <= 0 || sum > (player.SelectedCharacter?.LevelUpPoints ?? 0)
            ? player.GetLocalizedMessage("AdvisorInsufficientPoints")
            : string.Empty;
    }

    /// <summary>Validates the configured stat reset and its costs.</summary>
    /// <param name="player">The player whose reset is being checked.</param>
    /// <returns>An empty string when valid, otherwise a localized failure.</returns>
    internal static string ResetFailure(Player player)
    {
        var configuration = player.GameContext.FeaturePlugIns.GetPlugIn<StatResetFeaturePlugIn>()?.Configuration;
        if (configuration is null)
        {
            return player.GetLocalizedMessage(nameof(PlayerMessage.StatResetNotEnabled));
        }

        if (player.Inventory?.Items.Any(i => i.ItemSlot <= InventoryConstants.LastEquippableItemSlotIndex) == true)
        {
            return player.GetLocalizedMessage(nameof(PlayerMessage.RemoveEquipmentForStatReset));
        }

        if (!player.IsAtSafezone())
        {
            return player.GetLocalizedMessage(nameof(PlayerMessage.CantResetStatsNotInSafezone));
        }

        if (player.Level < configuration.RequiredLevel)
        {
            return player.GetLocalizedMessage(nameof(PlayerMessage.RequiredLevelForStatReset), configuration.RequiredLevel);
        }

        if (player.Money < configuration.RequiredMoney)
        {
            return player.GetLocalizedMessage(nameof(PlayerMessage.NotEnoughMoneyForStatReset), configuration.RequiredMoney);
        }

        if (configuration.RequiredResetItem is { } item
            && player.Inventory?.Items.Any(i => i.ItemSlot > InventoryConstants.LastEquippableItemSlotIndex
                && i.Definition is { } d && d.Group == item.Group && d.Number == item.Number) != true)
        {
            return player.GetLocalizedMessage(
                nameof(PlayerMessage.NotEnoughItemsForStatReset),
                1,
                new LocalizedString(item.Name).GetTranslation(player.Culture));
        }

        var refunded = GetRefund(player);
        return refunded + (long)(player.SelectedCharacter?.LevelUpPoints ?? 0) > int.MaxValue
            ? player.GetLocalizedMessage("AdvisorInvalidAllocation")
            : string.Empty;
    }

    /// <summary>Creates the localized reset quote shown before confirmation.</summary>
    /// <param name="player">The player whose live values are quoted.</param>
    /// <returns>The quote text.</returns>
    internal static string ResetDescription(Player player)
    {
        var configuration = player.GameContext.FeaturePlugIns.GetPlugIn<StatResetFeaturePlugIn>()?.Configuration;
        if (configuration is null)
        {
            return player.GetLocalizedMessage(nameof(PlayerMessage.StatResetNotEnabled));
        }

        var lines = new List<string>
        {
            player.GetLocalizedMessage("AdvisorResetRequirements", configuration.RequiredLevel, configuration.RequiredMoney),
            player.GetLocalizedMessage("AdvisorResetRefund", GetRefund(player), (long)player.SelectedCharacter!.LevelUpPoints + GetRefund(player)),
            player.GetLocalizedMessage("AdvisorResetEquipment"),
        };
        if (configuration.RequiredResetItem is { } item)
        {
            lines.Add(player.GetLocalizedMessage("AdvisorResetItem", new LocalizedString(item.Name).GetTranslation(player.Culture)));
        }

        if (configuration.MoveHome)
        {
            lines.Add(player.GetLocalizedMessage(nameof(PlayerMessage.ResetNpcMoveHome)));
        }

        if (configuration.LogOut)
        {
            lines.Add(player.GetLocalizedMessage(nameof(PlayerMessage.ResetNpcLogout)));
        }

        lines.Add(player.GetLocalizedMessage("AdvisorResetNotice"));
        return string.Join("\n", lines);
    }

    /// <summary>Creates a stable quote identity for one character state.</summary>
    /// <param name="player">The player whose state is bound to the quote.</param>
    /// <param name="reset">Whether reset rules and costs are also included.</param>
    /// <returns>A value used to invalidate stale confirmations.</returns>
    internal static string Fingerprint(Player player, bool reset)
    {
        var stats = string.Join(",", GetStats(player).Select(s => $"{s.Id}:{s.Current}:{s.Maximum}"));
        var value = $"{player.SelectedCharacter!.CharacterClass?.Number}|{player.SelectedCharacter.LevelUpPoints}|{stats}";
        if (!reset)
        {
            return value;
        }

        var config = player.GameContext.FeaturePlugIns.GetPlugIn<StatResetFeaturePlugIn>()?.Configuration;
        return $"{value}|{player.Level}|{player.Money}|{GetRefund(player)}|{config?.RequiredLevel}|{config?.RequiredMoney}|{config?.MoveHome}|{config?.LogOut}|{config?.RequiredResetItem?.Group}|{config?.RequiredResetItem?.Number}";
    }

    private static long GetRefund(Player player) => player.SelectedCharacter!.CharacterClass!.StatAttributes
        .Where(s => s.IncreasableByPlayer && s.Attribute is not null)
        .Sum(s => Math.Max(0L, (long)player.Attributes![s.Attribute!] - (long)s.BaseValue));
}
