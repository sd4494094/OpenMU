// <copyright file="BotNamingResources.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Properties;

using System.Globalization;
using System.Resources;

/// <summary>Localized labels for the optional complete bot nickname inventory.</summary>
public static class BotNamingResources
{
    private static readonly ResourceManager Manager = new(typeof(BotNamingResources));

    /// <summary>Gets the inventory validation message.</summary>
    public static string CharacterNamesValidation => Manager.GetString(nameof(CharacterNamesValidation), CultureInfo.CurrentUICulture)!;

    /// <summary>Gets the inventory caption.</summary>
    public static string CharacterNamesCaption => Manager.GetString(nameof(CharacterNamesCaption), CultureInfo.CurrentUICulture)!;

    /// <summary>Gets the inventory description.</summary>
    public static string CharacterNamesDescription => Manager.GetString(nameof(CharacterNamesDescription), CultureInfo.CurrentUICulture)!;
}
