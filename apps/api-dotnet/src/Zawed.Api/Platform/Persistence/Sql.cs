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

    /// <summary>Java's <c>Character.isWhitespace</c> (<see cref="Text.JavaText.IsWhitespace"/>).</summary>
    public static bool IsJavaWhitespace(char c) => Text.JavaText.IsWhitespace(c);

    [GeneratedRegex("[ \t\n\u000B\f\r]+", RegexOptions.CultureInvariant)]
    private static partial Regex AsciiWhitespace();
}
