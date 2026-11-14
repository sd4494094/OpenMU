// <copyright file="MiniGameDefinitionExtensions.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.MiniGames;

using MUnique.OpenMU.GameLogic.Attributes;

/// <summary>
/// Extensions regarding the <see cref="MiniGameDefinition"/>s.
/// </summary>
public static class MiniGameDefinitionExtensions
{
    /// <summary>
    /// Gets the suitable mini game definition for the player and mini game type.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="miniGameType">Type of the mini game.</param>
    /// <param name="eventLevel">The event level.</param>
    /// <returns>The suitable mini game definition.</returns>
    public static MiniGameDefinition? GetSuitableMiniGameDefinition(this Player player, MiniGameType miniGameType, byte eventLevel)
    {
        var definitions = player.GameContext.Configuration.MiniGameDefinitions
            .Where(def => def.Type == miniGameType && def.IsAvailable())
            .ToList();
        return definitions
                   .OrderByDescending(def => def.GameLevel)
                   .FirstOrDefault(def => def.IsInLevelRange(player))
               ?? definitions.FirstOrDefault(def => def.GameLevel == eventLevel);
    }

    /// <summary>
    /// Determines whether the mini game is available in the game, which requires that
    /// it's active, and that it has an entrance map which is active, too.
    /// </summary>
    /// <param name="definition">The definition of the mini game.</param>
    /// <returns><c>true</c> if the mini game is available; otherwise, <c>false</c>.</returns>
    public static bool IsAvailable(this MiniGameDefinition definition)
    {
        return definition.IsActive && definition.Entrance?.Map is { IsActive: true };
    }

    /// <summary>
    /// Determines whether the specified player is in level range of the mini game.
    /// </summary>
    /// <param name="definition">The definition of the mini game.</param>
    /// <param name="player">The player.</param>
    /// <returns><c>true</c> if the specified player is in level range; otherwise, <c>false</c>.</returns>
    public static bool IsInLevelRange(this MiniGameDefinition definition, Player player)
    {
        return player.SelectedCharacter is { } character
            && definition.IsInLevelRange(player.Level, character.IsSpecialCharacter(), character.CharacterClass?.IsMasterClass is true);
    }

    /// <summary>Evaluates detached public admission facts using the same rule as live players.</summary>
    /// <param name="definition">The configured event tier.</param>
    /// <param name="characterLevel">The normal character level.</param>
    /// <param name="isSpecialCharacter">Whether the configured class uses reduced event requirements.</param>
    /// <param name="isMasterClass">Whether the configured class is a master class.</param>
    /// <returns>Whether this class and normal level satisfy the tier's inclusive limits.</returns>
    public static bool IsInLevelRange(this MiniGameDefinition definition, int characterLevel, bool isSpecialCharacter, bool isMasterClass)
    {
        if (definition.RequiresMasterClass && !isMasterClass)
        {
            return false;
        }

        if (isSpecialCharacter)
        {
            return characterLevel >= definition.MinimumSpecialCharacterLevel
                   && characterLevel <= definition.MaximumSpecialCharacterLevel;
        }

        return characterLevel >= definition.MinimumCharacterLevel
               && characterLevel <= definition.MaximumCharacterLevel;
    }
}