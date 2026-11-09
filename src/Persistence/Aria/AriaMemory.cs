// <copyright file="AriaMemory.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Aria;

/// <summary>A bounded snapshot of one character's memory for one AI persona.</summary>
/// <param name="Summary">The long-term summary.</param>
/// <param name="ThroughId">The last summarized turn.</param>
/// <param name="Recent">The most recent turns, oldest first.</param>
/// <param name="Pending">The oldest unsummarized turns, oldest first.</param>
public sealed record AriaMemory(string Summary, long ThroughId, IReadOnlyList<AriaTurn> Recent, IReadOnlyList<AriaTurn> Pending);
