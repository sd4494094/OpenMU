// <copyright file="IAriaModelClient.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Startup.Aria;

using System.Threading;

/// <summary>Collects a complete model answer without exposing partial output to players.</summary>
public interface IAriaModelClient
{
    /// <summary>Generates a complete response.</summary>
    /// <param name="instructions">The trusted role instructions.</param>
    /// <param name="messages">The detached conversation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Only a successful final answer.</returns>
    ValueTask<string> CompleteAsync(string instructions, IReadOnlyList<AriaMessage> messages, CancellationToken cancellationToken);
}
