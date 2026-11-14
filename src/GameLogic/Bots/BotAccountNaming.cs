// <copyright file="BotAccountNaming.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using System.Globalization;

/// <summary>The persistent generated-login contract shared by generation and administration.</summary>
public static class BotAccountNaming
{
    /// <summary>Gets the fixed prefix of existing generated accounts; changing it would orphan the population.</summary>
    public static string Prefix => "bot";

    /// <summary>Gets the persistent login for a one-based generated account index.</summary>
    /// <param name="index">The one-based account index.</param>
    /// <returns>The internal login.</returns>
    public static string GetLoginName(int index) => $"{Prefix}{index:D4}";

    /// <summary>Gets a canonical generated index, or zero for an unrelated login.</summary>
    /// <param name="login">The account login.</param>
    /// <returns>The one-based index, or zero.</returns>
    public static int GetIndex(string login)
        => login.StartsWith(Prefix, StringComparison.Ordinal)
            && int.TryParse(login.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            && index > 0 && login == GetLoginName(index) ? index : 0;
}
