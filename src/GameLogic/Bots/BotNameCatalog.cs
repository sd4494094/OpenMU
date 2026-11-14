// <copyright file="BotNameCatalog.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using System.IO;

/// <summary>Validates complete, reviewed nicknames against the unchanged UTF-8 name fields.</summary>
internal static class BotNameCatalog
{
    /// <summary>The Season 6 character-name fields carry ten UTF-8 bytes, not ten Unicode characters.</summary>
    internal const int MaximumNameBytes = 10;

    private static readonly string[] ReservedNames = ["gm", "admin", "administrator", "客服", "管理员", "系统", "官方", "创世神", "艾莉雅"];

    /// <summary>Gets the reviewed default inventory, independent of the host's current culture.</summary>
    internal static IReadOnlyList<string> DefaultNames { get; } = LoadDefaultNames();

    /// <summary>Validates the entire inventory before any account can be generated.</summary>
    /// <param name="names">Complete nicknames; an empty inventory selects the reviewed default.</param>
    /// <returns>A copy without duplicates or unsafe names.</returns>
    internal static string[] Validate(IEnumerable<string>? names)
    {
        var entries = names?.ToArray();
        if (entries is not { Length: > 0 })
        {
            entries = DefaultNames.ToArray();
        }

        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in entries)
        {
            if (!IsValid(name) || !unique.Add(name))
            {
                throw new InvalidOperationException("The bot nickname inventory contains an invalid, reserved or duplicate name.");
            }
        }

        return entries;
    }

    /// <summary>Checks the common alphabet and the byte limit before a nickname reaches any packet.</summary>
    /// <param name="name">The complete nickname.</param>
    /// <returns>Whether the nickname is safe for the current name fields.</returns>
    internal static bool IsValid(string? name)
        => name is { Length: >= 2 and <= MaximumNameBytes }
            && name.All(character => character is >= '\u4e00' and <= '\u9fff' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')
            && Encoding.UTF8.GetByteCount(name) <= MaximumNameBytes
            && !ReservedNames.Any(reserved => name.StartsWith(reserved, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> LoadDefaultNames()
    {
        using var stream = typeof(BotNameCatalog).Assembly.GetManifestResourceStream("MUnique.OpenMU.GameLogic.Bots.CharacterNames.txt")
            ?? throw new InvalidOperationException("The reviewed bot nickname inventory is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Array.AsReadOnly(reader.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }
}
