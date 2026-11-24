// <copyright file="ICastleSiegeBattleStatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Views.CastleSiege;

/// <summary>
/// Updates the client's active battle state on the Castle Siege map.
/// </summary>
public interface ICastleSiegeBattleStatePlugIn : IViewPlugIn
{
    /// <summary>
    /// Enables or disables the client's battle features.
    /// </summary>
    /// <param name="isStarted">Whether the battle is in progress.</param>
    /// <returns>A task which represents the update.</returns>
    ValueTask ShowCastleSiegeBattleStateAsync(bool isStarted);
}
