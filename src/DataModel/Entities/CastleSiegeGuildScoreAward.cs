// <copyright file="CastleSiegeGuildScoreAward.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.DataModel.Entities;

/// <summary>
/// Records one guild's Castle Siege score award for a battle.
/// </summary>
[AggregateRoot]
public class CastleSiegeGuildScoreAward
{
    /// <summary>
    /// Gets or sets the unique identifier of this record.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the persistent identifier of the guild receiving the award.
    /// </summary>
    public Guid GuildId { get; set; }

    /// <summary>
    /// Gets or sets the start time of the battle which created the award.
    /// </summary>
    public DateTime BattleStartUtc { get; set; }

    /// <summary>
    /// Gets or sets the amount added to the guild score.
    /// </summary>
    public int Score { get; set; }
}
