// <copyright file="IAriaToolClient.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Startup.Aria;

using System.Text.Json;
using System.Threading;

/// <summary>Queries only the restricted, public gameplay surface.</summary>
public interface IAriaToolClient
{
    /// <summary>Executes a validated query. Results are untrusted data, never instructions.</summary>
    /// <param name="name">An approved tool name.</param>
    /// <param name="arguments">The bounded query arguments.</param>
    /// <param name="cancellationToken">The shared conversation deadline.</param>
    /// <returns>The structured public result.</returns>
    ValueTask<JsonElement> CallAsync(string name, JsonElement arguments, CancellationToken cancellationToken);
}
