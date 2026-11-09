// <copyright file="AriaTurn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Aria;

/// <summary>A completed turn copied across persistence contexts.</summary>
/// <param name="Id">The monotonically increasing record id.</param>
/// <param name="Question">The player text.</param>
/// <param name="Answer">The delivered answer.</param>
public sealed record AriaTurn(long Id, string Question, string Answer);
