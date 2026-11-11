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

    /// <summary>Display budget including the sender, calibrated to the existing chat panel.</summary>
    private const int DisplayColumns = 64;

    /// <summary>Payload headroom below the supported client's 90-byte receive buffer.</summary>
    private const int MaximumLineBytes = 80;

    /// <summary>Only seek a natural boundary within four wide characters of a full line.</summary>
    private const int NaturalBreakSearchColumns = 8;

    /// <summary>
    /// Splits completed text for the narrow legacy chat panel, including its sender label.
    /// </summary>
    /// <param name="text">The complete message, without channel prefixes.</param>
    /// <param name="sender">The displayed sender.</param>
    /// <returns>Ordered lines; concatenating them preserves the complete message.</returns>
    /// <remarks>
    /// The supported client wraps at most twice and treats spaces as word boundaries.
    /// Keep each packet below its 90-byte receive buffer and use a conservative
    /// 64-column display budget, reserving space for the sender and separator.
    /// Wide Unicode elements count as two columns. This is a compatibility bound,
    /// not a change to the model's total answer budget. The byte budget allows
    /// at most 26 plain Chinese characters per packet; only nearby punctuation
    /// is preferred, so an early clause does not leave most of a line empty.
    /// </remarks>
    public static IReadOnlyList<string> SplitForChat(string text, string sender)
    {
        var lines = new List<string>();
        var senderWidth = sender.EnumerateRunes().Sum(rune => rune.Value > 0xFF ? 2 : 1);
        var available = Math.Max(8, DisplayColumns - senderWidth - 3);
        var elements = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            elements.Add(enumerator.GetTextElement());
        }

        for (var start = 0; start < elements.Count;)
        {
            var end = start;
            var columns = 0;
            var bytes = 0;
            while (end < elements.Count)
            {
                var width = elements[end].EnumerateRunes().Sum(rune => rune.Value > 0xFF ? 2 : 1);
                var size = Encoding.UTF8.GetByteCount(elements[end]);
                if (end > start && (columns + width > available || bytes + size > MaximumLineBytes))
                {
                    break;
                }

                columns += width;
                bytes += size;
                end++;
            }

            if (end < elements.Count)
            {
                end = FindLineBreak(elements, start, end);
            }

            lines.Add(string.Concat(elements.Skip(start).Take(end - start)));
            start = end;
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

    private static int FindLineBreak(IReadOnlyList<string> elements, int start, int end)
    {
        // Prefer the closest natural boundary only near the fitted line's end.
        // Earlier punctuation must not recreate the short lines seen in the client.
        var remainingColumns = 0;
        for (var index = end; index > start && remainingColumns <= NaturalBreakSearchColumns; index--)
        {
            var previous = elements[index - 1];
            if (!IsClosingPunctuation(elements[index]) && !IsOpeningPunctuation(previous)
                && (char.IsWhiteSpace(previous, 0) || IsClosingPunctuation(previous))
                && !IsNumericSeparator(elements, index - 1))
            {
                return index;
            }

            remainingColumns += previous.EnumerateRunes().Sum(rune => rune.Value > 0xFF ? 2 : 1);
        }

        // Keep an ASCII word/coordinate intact if it fits on the following line.
        var cut = end;
        while (cut > start + 1 && (IsClosingPunctuation(elements[cut]) || IsOpeningPunctuation(elements[cut - 1])))
        {
            cut--;
        }

        if (IsAsciiToken(elements[cut - 1]) && IsAsciiToken(elements[cut]))
        {
            var tokenStart = cut - 1;
            while (tokenStart > start && IsAsciiToken(elements[tokenStart - 1]))
            {
                tokenStart--;
            }

            if (tokenStart > start)
            {
                cut = tokenStart;
            }
        }

        return cut;
    }

    private static bool IsClosingPunctuation(string element)
        => element.Length == 1 && "，。！？；：、,.!?;:）】》」』”’)]}>…".Contains(element[0]);

    private static bool IsOpeningPunctuation(string element)
        => element.Length == 1 && "（【《「『“‘([{<".Contains(element[0]);

    private static bool IsAsciiToken(string element)
        => element.Length == 1 && (char.IsAsciiLetterOrDigit(element[0]) || element[0] is '_' or '-' or '.' or ',' or ':');

    private static bool IsNumericSeparator(IReadOnlyList<string> elements, int index)
        => index > 0 && index + 1 < elements.Count && elements[index] is "," or "." or ":"
            && char.IsDigit(elements[index - 1], 0) && char.IsDigit(elements[index + 1], 0);
}
