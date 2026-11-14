// <copyright file="GameplayContentNames.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel.Operations;

using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using MUnique.OpenMU.Interfaces;

/// <summary>Searches stored translations and exact built-in resource aliases without changing persisted names.</summary>
internal static class GameplayContentNames
{
    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");
    private static readonly ConcurrentDictionary<(string Source, string Neutral), LocalizedString> SourceAliases = new();

    /// <summary>Resolves registered resource translations while preserving stored overrides.</summary>
    /// <param name="source">The registered resource family.</param>
    /// <param name="name">The persisted name.</param>
    /// <returns>The name enriched with exact resource translations.</returns>
    internal static LocalizedString Resolve(string source, LocalizedString name)
    {
        if (name.SourceKey is not null && name.GetFromSource() is { } current)
        {
            return Merge(name, current);
        }

        // Old databases can contain English-only names without source metadata. Only an
        // exact neutral-name match may supply aliases; never guess from an item number.
        var aliases = SourceAliases.GetOrAdd((source, name.ValueInNeutralLanguage), key =>
        {
            if (LocalizedStringResources.TryResolve(key.Source + "/_", out var manager, out _)
                && manager.GetResourceSet(CultureInfo.InvariantCulture, true, false) is { } set)
            {
                var matches = set.Cast<DictionaryEntry>()
                    .Where(entry => entry.Value is string text && text.Equals(key.Neutral, StringComparison.Ordinal))
                    .Select(entry => (string)entry.Key).Take(2).ToArray();
                if (matches.Length == 1)
                {
                    return manager.GetLocalizedString(matches[0]);
                }
            }

            return default;
        });
        return Merge(name, aliases);
    }

    /// <summary>Gets the Chinese name, falling back to the neutral text.</summary>
    /// <param name="name">The resolved name.</param>
    /// <returns>The display name.</returns>
    internal static string Display(LocalizedString name)
        => name.GetTranslation(Chinese) ?? name.ValueInNeutralLanguage;

    /// <summary>Gets distinct neutral and translated aliases without resource metadata.</summary>
    /// <param name="name">The resolved name.</param>
    /// <returns>The searchable aliases.</returns>
    internal static string[] Aliases(LocalizedString name)
        => name.GetTranslations().Select(pair => pair.Value).Prepend(name.ValueInNeutralLanguage)
            .SelectMany(value => value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Matches partial names or an exact invariant content key.</summary>
    /// <param name="name">The resolved name.</param>
    /// <param name="text">The player filter.</param>
    /// <param name="key">The invariant key.</param>
    /// <returns>Whether an alias or key matches.</returns>
    internal static bool Matches(LocalizedString name, string text, string key)
    {
        var query = Normalize(text);
        return query.Length > 0 && (Normalize(key).Equals(query, StringComparison.OrdinalIgnoreCase)
            || Aliases(name).Any(alias => Normalize(alias).Contains(query, StringComparison.OrdinalIgnoreCase)));
    }

    private static string Normalize(string value)
    {
        var normalized = string.Concat(value.Trim().Where(character => !char.IsWhiteSpace(character)));

        // Players call skill-learning orbs both "之石" and "技能石".
        if (normalized.EndsWith("技能石", StringComparison.Ordinal))
        {
            normalized = normalized[..^3];
        }

        return normalized;
    }

    private static LocalizedString Merge(LocalizedString stored, LocalizedString source)
    {
        foreach (var translation in source.GetTranslations())
        {
            var culture = CultureInfo.GetCultureInfo(translation.Key);
            if (stored.GetOwnTranslation(culture) is null)
            {
                stored = stored.WithTranslation(culture, translation.Value);
            }
        }

        return stored;
    }
}
