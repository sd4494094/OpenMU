// <copyright file="ResetNpcDescription.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Resets;

using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.Interfaces;

/// <summary>
/// Formats the same live configuration and progression used when executing a reset.
/// </summary>
internal static class ResetNpcDescription
{
    /// <summary>
    /// Creates the localized requirements and consequences for the selected character.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="configuration">The active reset rules.</param>
    /// <returns>The description.</returns>
    public static string Create(Player player, ResetConfiguration configuration)
    {
        if (player.Attributes is null || player.SelectedCharacter is null)
        {
            return player.GetLocalizedMessage(nameof(PlayerMessage.NotEnteredTheGame));
        }

        var progression = ResetProgressionCalculator.Calculate(
            (int)player.Attributes[Stats.Resets],
            (int)player.Attributes[Stats.PointsPerReset],
            configuration);
        var lines = new List<string>
        {
            player.GetLocalizedMessage(nameof(PlayerMessage.ResetNpcNext), progression.NextResetCount, player.Level, configuration.RequiredLevel),
            player.GetLocalizedMessage(nameof(PlayerMessage.ResetNpcMoney), progression.RequiredZen, player.Money),
        };
        if (configuration.RequiredResetItem is { } item && progression.RequiredItemAmount > 0)
        {
            var held = player.Inventory?.Items.Count(i => i.Definition is { } d && d.Group == item.Group && d.Number == item.Number) ?? 0;
            lines.Add(player.GetLocalizedMessage(nameof(PlayerMessage.ResetNpcItems), new LocalizedString(item.Name).GetTranslation(player.Culture), progression.RequiredItemAmount, held));
        }

        if (configuration.ResetLimit is > 0)
        {
            lines.Add(player.GetLocalizedMessage(nameof(PlayerMessage.ResetNpcLimit), configuration.ResetLimit));
        }

        var resultingPoints = configuration.ReplacePointsPerReset
            ? progression.TotalPointsAfterReset
            : player.SelectedCharacter.LevelUpPoints + progression.PointsForReset;
        lines.Add(player.GetLocalizedMessage(nameof(PlayerMessage.ResetNpcResult), configuration.LevelAfterReset, progression.PointsForReset, resultingPoints));
        lines.Add(player.GetLocalizedMessage(configuration.ResetStats ? nameof(PlayerMessage.ResetNpcStatsReset) : nameof(PlayerMessage.ResetNpcStatsKept)));
        if (configuration.MoveHome)
        {
            lines.Add(player.GetLocalizedMessage(nameof(PlayerMessage.ResetNpcMoveHome)));
        }

        if (configuration.LogOut)
        {
            lines.Add(player.GetLocalizedMessage(nameof(PlayerMessage.ResetNpcLogout)));
        }

        lines.Add(player.GetLocalizedMessage(nameof(PlayerMessage.ResetNpcConfirmationNotice)));
        return string.Join("\n", lines);
    }
}
