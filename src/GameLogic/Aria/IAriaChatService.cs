// <copyright file="IAriaChatService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Aria;

/// <summary>Accepts Aria messages without waiting for database or model operations.</summary>
public interface IAriaChatService
{
    /// <summary>Queues a message and returns a localized resource key on rejection.</summary>
    /// <param name="player">The submitting player, already in its exclusive context.</param>
    /// <param name="question">The question or memory deletion command.</param>
    /// <returns>A rejection resource key, or null when queued.</returns>
    string? TrySubmit(Player player, string question);
}
