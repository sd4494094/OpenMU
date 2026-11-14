// <copyright file="BotNameGenerator.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using System.Threading;
using MUnique.OpenMU.Persistence;

/// <summary>Allocates complete reviewed nicknames without synthesizing suffixes or fallback names.</summary>
/// <remarks>
/// Existing characters keep their persisted names. An inventory is finite: exhausting it must never
/// change an existing character or silently escape the reviewed list. The database's unique name index
/// remains the final concurrency boundary; the elected generator in BotServerPartition owns allocation.
/// </remarks>
internal sealed class BotNameGenerator
{
    private readonly List<string> _availableNames;

    /// <summary>Initializes a new instance of the <see cref="BotNameGenerator"/> class with a shuffled, validated inventory.</summary>
    /// <param name="names">The configured names, or the reviewed default when empty.</param>
    internal BotNameGenerator(IEnumerable<string>? names = null)
    {
        this._availableNames = BotNameCatalog.Validate(names).ToList();
        for (var index = this._availableNames.Count - 1; index > 0; index--)
        {
            var other = Rand.NextInt(0, index + 1);
            (this._availableNames[index], this._availableNames[other]) = (this._availableNames[other], this._availableNames[index]);
        }
    }

    /// <summary>Allocates an unused name, optionally preserving the requested elf build.</summary>
    /// <param name="context">The shared character-name lookup.</param>
    /// <param name="reserved">Names already allocated in this generation pass.</param>
    /// <param name="requiredVariant">The required build variant, or null for either.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A name from the inventory, reserved for the caller.</returns>
    public async ValueTask<string> GenerateUniqueAsync(IPlayerContext context, ISet<string> reserved, int? requiredVariant = null, CancellationToken cancellationToken = default)
    {
        for (var index = this._availableNames.Count - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = this._availableNames[index];
            if (requiredVariant is { } variant && BotBuild.GetVariant(name) != variant)
            {
                continue;
            }

            if (reserved.Contains(name))
            {
                this._availableNames.RemoveAt(index);
                continue;
            }

            var existing = await context.GetAccountByCharacterNameAsync(name, cancellationToken).ConfigureAwait(false);
            this._availableNames.RemoveAt(index);
            if (existing is null && reserved.Add(name))
            {
                return name;
            }
        }

        throw new InvalidOperationException("No unused reviewed bot nickname remains for the requested build. Extend the nickname inventory before generating more bots.");
    }
}
