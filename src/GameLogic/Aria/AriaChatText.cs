// <copyright file="AriaChatText.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Aria;

using System.Globalization;

/// <summary>Constrains a single Aria answer to the supported chat clients.</summary>
public static class AriaChatText
{
    /// <summary>Maximum reply UTF-16 units, fitting the supported clients' chat buffers.</summary>
    public const int MaximumLength = 80;

    /// <summary>Conservative UTF-8 budget for the original C1 chat message.</summary>
    public const int MaximumBytes = 240;

    /// <summary>
    /// Splits completed text for the narrow legacy chat panel, including its sender label.
    /// </summary>
    /// <param name="text">The complete message, without channel prefixes.</param>
    /// <param name="sender">The displayed sender.</param>
    /// <returns>Ordered lines; concatenating them preserves the complete message.</returns>
    /// <remarks>
    /// The supported client wraps at most twice and treats spaces as word boundaries.
    /// Keep each packet below its 90-byte receive buffer and use a conservative
    /// 40-column display budget, reserving space for the sender and separator.
    /// Wide Unicode elements count as two columns. This is a compatibility bound,
    /// not a change to the model's total answer budget.
    /// </remarks>
    public static IReadOnlyList<string> SplitForChat(string text, string sender)
    {
        var lines = new List<string>();
        var senderWidth = sender.EnumerateRunes().Sum(rune => rune.Value > 0xFF ? 2 : 1);
        var available = Math.Max(8, 40 - senderWidth - 3);
        var line = new StringBuilder();
        var columns = 0;
        var bytes = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            var width = element.EnumerateRunes().Sum(rune => rune.Value > 0xFF ? 2 : 1);
            var size = Encoding.UTF8.GetByteCount(element);
            if (line.Length > 0 && (columns + width > available || bytes + size > 80))
            {
                lines.Add(line.ToString());
                line.Clear();
                columns = 0;
                bytes = 0;
            }

            line.Append(element);
            columns += width;
            bytes += size;
        }

        if (line.Length > 0)
        {
            lines.Add(line.ToString());
        }

        return lines;
    }

    /// <summary>Normalizes and bounds an answer without splitting a Unicode text element.</summary>
    /// <param name="text">The model output.</param>
    /// <returns>A single bounded plain text answer.</returns>
    public static string Constrain(string text)
    {
        var clean = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsControl(rune) || Rune.IsWhiteSpace(rune))
            {
                if (clean.Length > 0 && clean[^1] != ' ')
                {
                    clean.Append(' ');
                }
            }
            else if (Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.OtherSymbol or UnicodeCategory.Format or UnicodeCategory.PrivateUse)
                     && rune.Value is not (0xFFFD or '*' or '`' or '#' or '~' or '@' or '$' or '/'))
            {
                clean.Append(rune.ToString());
            }
        }

        var normalized = clean.ToString().Trim();
        if (normalized.Length <= MaximumLength && Encoding.UTF8.GetByteCount(normalized) <= MaximumBytes)
        {
            return normalized;
        }

        var result = new StringBuilder();
        var elements = StringInfo.GetTextElementEnumerator(normalized);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (result.Length + element.Length > MaximumLength - 1
                || Encoding.UTF8.GetByteCount(result.ToString()) + Encoding.UTF8.GetByteCount(element) > MaximumBytes - 3)
            {
                break;
            }

            result.Append(element);
        }

        // Prefer a complete sentence; otherwise do not leave a cut number or coordinate.
        var value = result.ToString();
        var sentence = value.LastIndexOfAny(['。', '！', '？', '!', '?']);
        if (sentence >= value.Length / 2)
        {
            return value[..(sentence + 1)];
        }

        while (result.Length > 0 && (char.IsDigit(result[^1]) || result[^1] is ',' or '.' or '(' or '（'))
        {
            result.Length--;
        }

        return result.Append('…').ToString();
    }
}
