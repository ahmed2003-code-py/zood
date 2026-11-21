using System.Text.RegularExpressions;

namespace Zawed.Api.Platform.Persistence;

/// <summary>Small conversions every store needs (Java <c>platform.persistence.Sql</c>).</summary>
public static partial class Sql
{
    /// <summary>
    /// Trims, collapses runs of whitespace to one space, and turns an empty string into null, so optional text is
    /// stored one way only. Java's rules exactly: <c>String.strip()</c> (Unicode whitespace, but not the no-break
    /// spaces U+00A0, U+2007 and U+202F), then <c>\s+</c> (ASCII whitespace only) to a space.
    /// </summary>
    public static string? Clean(string? value)
    {
        if (value is null)
        {
            return null;
        }
        int start = 0, end = value.Length;
        while (start < end && IsJavaWhitespace(value[start]))
        {
            start++;
        }
        while (end > start && IsJavaWhitespace(value[end - 1]))
        {
            end--;
        }
        var trimmed = AsciiWhitespace().Replace(value[start..end], " ");
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>Java's <c>Character.isWhitespace</c>.</summary>
    private static bool IsJavaWhitespace(char c) => c switch
    {
        ' ' or ' ' or ' ' => false,
        '\t' or '\n' or '\u000B' or '\f' or '\r' or '\u001C' or '\u001D' or '\u001E' or '\u001F' => true,
        _ => char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.SpaceSeparator
            or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator,
    };

    [GeneratedRegex("[ \t\n\u000B\f\r]+", RegexOptions.CultureInvariant)]
    private static partial Regex AsciiWhitespace();
}
