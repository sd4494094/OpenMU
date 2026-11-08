// <copyright file="RecreatedCaptions.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests.Localization;

using System.Resources;

/// <summary>
/// Simulates concurrent first access to a generated resource getter returning different manager instances.
/// </summary>
internal static class RecreatedCaptions
{
    /// <summary>Gets a new manager for the same embedded resource on every access.</summary>
    public static ResourceManager ResourceManager => new("MUnique.OpenMU.Tests.Localization.TestCaptions", typeof(TestCaptions).Assembly);

    /// <summary>Gets the resource property used by the expression-based localization API.</summary>
    public static string Lorencia => "Lorencia";
}
