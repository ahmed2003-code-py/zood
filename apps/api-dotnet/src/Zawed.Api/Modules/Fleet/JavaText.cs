using System.Globalization;

namespace Zawed.Api.Modules.Fleet;

/// <summary>Java's text rules where the port must match them exactly: <c>trim()</c>, <c>strip()</c>, <c>isBlank()</c>.</summary>
internal static class JavaText
{
    private static readonly char[] Trimmed = [.. Enumerable.Range(0, 33).Select(c => (char)c)];

    /// <summary><c>String.trim()</c>: every character up to U+0020 off both ends, as the Java controllers trimmed a reason.</summary>
    public static string Trim(string value) => value.Trim(Trimmed);

    /// <summary><c>String.strip()</c>: <see cref="IsWhitespace"/> off both ends.</summary>
    public static string Strip(string value)
    {
        int start = 0, end = value.Length;
        while (start < end && IsWhitespace(value[start]))
        {
            start++;
        }
        while (end > start && IsWhitespace(value[end - 1]))
        {
            end--;
        }
        return value[start..end];
    }

    /// <summary><c>String.isBlank()</c>.</summary>
    public static bool IsBlank(string value) => value.All(IsWhitespace);

    /// <summary><c>Character.isWhitespace</c>: Unicode spaces except the no-break ones, and the ASCII separators.</summary>
    public static bool IsWhitespace(char c) => c switch
    {
        ' ' or ' ' or ' ' => false,
        '\t' or '\n' or '\u000B' or '\f' or '\r' or '\u001C' or '\u001D' or '\u001E' or '\u001F' => true,
        _ => char.GetUnicodeCategory(c) is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator,
    };
}
