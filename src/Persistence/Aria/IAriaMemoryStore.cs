// <copyright file="IAriaMemoryStore.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Aria;

using System.Threading;

/// <summary>Stores private NPC memory independently of the game entity graph.</summary>
public interface IAriaMemoryStore
{
    /// <summary>Loads bounded history and summary.</summary>
    /// <param name="characterId">The character id.</param>
    /// <param name="persona">The AI persona.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The detached memory.</returns>
    ValueTask<AriaMemory> LoadAsync(Guid characterId, string persona, CancellationToken cancellationToken);

    /// <summary>Appends a completed interaction.</summary>
    /// <param name="characterId">The character id.</param>
    /// <param name="persona">The AI persona.</param>
    /// <param name="question">The question.</param>
    /// <param name="answer">The bounded answer.</param>
    /// <param name="model">The model alias.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The inserted turn id.</returns>
    ValueTask<long> AppendAsync(Guid characterId, string persona, string question, string answer, string model, CancellationToken cancellationToken);

    /// <summary>Updates a summary only if its previous watermark matches.</summary>
    /// <param name="characterId">The character id.</param>
    /// <param name="persona">The AI persona.</param>
    /// <param name="previousThroughId">The old watermark.</param>
    /// <param name="throughId">The new watermark.</param>
    /// <param name="summary">The summary.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    ValueTask SummarizeAsync(Guid characterId, string persona, long previousThroughId, long throughId, string summary, CancellationToken cancellationToken);

    /// <summary>Deletes history and summary for this character and persona.</summary>
    /// <param name="characterId">The character id.</param>
    /// <param name="persona">The AI persona.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    ValueTask ForgetAsync(Guid characterId, string persona, CancellationToken cancellationToken);

    /// <summary>Removes expired raw conversations.</summary>
    /// <param name="retentionDays">The retention in days.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    ValueTask PruneAsync(int retentionDays, CancellationToken cancellationToken);
}
