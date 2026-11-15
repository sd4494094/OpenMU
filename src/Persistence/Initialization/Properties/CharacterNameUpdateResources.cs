// <copyright file="CharacterNameUpdateResources.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Properties;

using System.Globalization;
using System.Resources;

/// <summary>
/// Localized operator text for the optional character name policy update.
/// </summary>
public static class CharacterNameUpdateResources
{
    private static readonly ResourceManager Resources = new(typeof(CharacterNameUpdateResources));

    /// <summary>Gets the update name.</summary>
    public static string Name => Resources.GetString(nameof(Name), CultureInfo.CurrentUICulture)!;

    /// <summary>Gets the update description.</summary>
    public static string Description => Resources.GetString(nameof(Description), CultureInfo.CurrentUICulture)!;
}
