// <copyright file="BotManagementResources.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Shared.Properties;

using System.Globalization;
using System.Resources;

/// <summary>Text for the bot population configuration preview.</summary>
public static class BotManagementResources
{
    private static readonly ResourceManager Manager = new(typeof(BotManagementResources));

    /// <summary>Resolves a management label in the current UI culture.</summary>
    /// <param name="name">The resource key.</param>
    /// <returns>The localized label.</returns>
    public static string Get(string name) => Manager.GetString(name, CultureInfo.CurrentUICulture) ?? name;
}
