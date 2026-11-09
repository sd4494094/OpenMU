// <copyright file="AriaResources.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Aria;

/// <summary>Localized metadata for the optional Aria command plugins.</summary>
public static class AriaResources
{
    /// <summary>Gets the localized command name.</summary>
    public static string CommandName => PlugInResources.ResourceManager.GetString("AriaChat_Name", PlugInResources.Culture) ?? string.Empty;

    /// <summary>Gets the localized command description.</summary>
    public static string CommandDescription => PlugInResources.ResourceManager.GetString("AriaChat_Description", PlugInResources.Culture) ?? string.Empty;
}
