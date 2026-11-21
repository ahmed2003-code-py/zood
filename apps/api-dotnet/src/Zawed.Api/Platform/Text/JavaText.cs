using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace Zawed.Api.Platform.Text;

/// <summary>
/// Java's text rules, where a .NET instance must read an input exactly as a Java instance did: during the cutover both
/// serve requests against one database, so an Idempotency-Key, an activation code or a receiving account must come out
/// the same on either. .NET's own <c>Trim()</c>, <c>IsNullOrWhiteSpace</c>, <c>ToUpperInvariant</c> and
/// <c>long.Parse</c> differ from Java at the edges (the no-break spaces, NEL, the dotless i, <c>ß</c>, non-ASCII
/// digits). Checked against JDK 25 by <c>JavaTextTests</c>.
/// </summary>
public static class JavaText
{
    private static readonly char[] UpToSpace = [.. Enumerable.Range(0, 33).Select(c => (char)c)];

    /// <summary>
    /// <c>Character.isWhitespace</c>: the Unicode space, line and paragraph separators except the no-break ones
    /// (U+00A0, U+2007, U+202F), and the ASCII separators U+0009 to U+000D and U+001C to U+001F. NEL (U+0085) is not one.
    /// </summary>
    public static bool IsWhitespace(char c) => c switch
    {
        '\u00A0' or '\u2007' or '\u202F' => false,
        '\t' or '\n' or '\u000B' or '\f' or '\r' or '\u001C' or '\u001D' or '\u001E' or '\u001F' => true,
        _ => char.GetUnicodeCategory(c) is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator,
    };

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

    /// <summary><c>String.isBlank()</c>: empty, or <see cref="IsWhitespace"/> only.</summary>
    public static bool IsBlank(string value) => value.All(IsWhitespace);

    /// <summary><c>String.trim()</c>: every character up to U+0020 off both ends, and nothing above it.</summary>
    public static string Trim(string value) => value.Trim(UpToSpace);

    /// <summary>
    /// <c>String.toUpperCase(Locale.ROOT)</c>: Unicode's full upper-case mapping, where one character may become several
    /// (<c>ß</c> → <c>SS</c>, <c>ﬁ</c> → <c>FI</c>) and the dotless i becomes I. .NET's invariant upper case is the
    /// simple mapping, one character for one, and leaves <c>ı</c> and <c>ſ</c> alone; the characters where the two differ
    /// (Unicode's SpecialCasing.txt, plus those two) are listed here, generated from JDK 25.
    /// </summary>
    public static string ToUpperRoot(string value)
    {
        var upper = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length;)
        {
            if (Rune.DecodeFromUtf16(value.AsSpan(i), out var rune, out var length) != System.Buffers.OperationStatus.Done)
            {
                upper.Append(value, i, length); // a lone surrogate stays as it is, as in Java
            }
            else if (FullMappings.TryGetValue(rune.Value, out var special))
            {
                upper.Append(special);
            }
            else
            {
                upper.Append(Rune.ToUpperInvariant(rune).ToString());
            }
            i += length;
        }
        return upper.ToString();
    }

    /// <summary>
    /// <c>Long.parseLong(value)</c>: an optional <c>+</c> or <c>-</c>, then one or more decimal digits of any script, as
    /// <c>Character.digit(char, 10)</c> reads them (<c>١٢٣</c> is 123), nothing else, within the range of a long.
    /// Read char by char, as Java does: a digit outside the BMP is no digit.
    /// </summary>
    public static bool TryParseLong(string value, out long result)
    {
        result = 0;
        var negative = value.Length > 0 && value[0] == '-';
        var start = value.Length > 0 && value[0] is '-' or '+' ? 1 : 0;
        if (value.Length == start)
        {
            return false;
        }
        // Accumulated negatively, as Java does, so long.MinValue fits.
        long total = 0;
        for (var i = start; i < value.Length; i++)
        {
            var digit = char.GetUnicodeCategory(value[i]) == UnicodeCategory.DecimalDigitNumber ? CharUnicodeInfo.GetDecimalDigitValue(value[i]) : -1;
            if (digit is < 0 or > 9 || total < long.MinValue / 10 || total * 10 < long.MinValue + digit)
            {
                return false;
            }
            total = total * 10 - digit;
        }
        if (!negative && total == long.MinValue)
        {
            return false;
        }
        result = negative ? total : -total;
        return true;
    }

    /// <summary>Where Java's root-locale upper case is not <see cref="Rune.ToUpperInvariant"/> (generated from JDK 25).</summary>
    private static readonly FrozenDictionary<int, string> FullMappings = new Dictionary<int, string>
    {
        [0x00DF] = "SS", [0x0131] = "I", [0x0149] = "\u02BCN", [0x017F] = "S", [0x01F0] = "J\u030C", [0x0390] = "\u0399\u0308\u0301",
        [0x03B0] = "\u03A5\u0308\u0301", [0x0587] = "\u0535\u0552", [0x1E96] = "H\u0331", [0x1E97] = "T\u0308", [0x1E98] = "W\u030A", [0x1E99] = "Y\u030A",
        [0x1E9A] = "A\u02BE", [0x1F50] = "\u03A5\u0313", [0x1F52] = "\u03A5\u0313\u0300", [0x1F54] = "\u03A5\u0313\u0301", [0x1F56] = "\u03A5\u0313\u0342", [0x1F80] = "\u1F08\u0399",
        [0x1F81] = "\u1F09\u0399", [0x1F82] = "\u1F0A\u0399", [0x1F83] = "\u1F0B\u0399", [0x1F84] = "\u1F0C\u0399", [0x1F85] = "\u1F0D\u0399", [0x1F86] = "\u1F0E\u0399",
        [0x1F87] = "\u1F0F\u0399", [0x1F88] = "\u1F08\u0399", [0x1F89] = "\u1F09\u0399", [0x1F8A] = "\u1F0A\u0399", [0x1F8B] = "\u1F0B\u0399", [0x1F8C] = "\u1F0C\u0399",
        [0x1F8D] = "\u1F0D\u0399", [0x1F8E] = "\u1F0E\u0399", [0x1F8F] = "\u1F0F\u0399", [0x1F90] = "\u1F28\u0399", [0x1F91] = "\u1F29\u0399", [0x1F92] = "\u1F2A\u0399",
        [0x1F93] = "\u1F2B\u0399", [0x1F94] = "\u1F2C\u0399", [0x1F95] = "\u1F2D\u0399", [0x1F96] = "\u1F2E\u0399", [0x1F97] = "\u1F2F\u0399", [0x1F98] = "\u1F28\u0399",
        [0x1F99] = "\u1F29\u0399", [0x1F9A] = "\u1F2A\u0399", [0x1F9B] = "\u1F2B\u0399", [0x1F9C] = "\u1F2C\u0399", [0x1F9D] = "\u1F2D\u0399", [0x1F9E] = "\u1F2E\u0399",
        [0x1F9F] = "\u1F2F\u0399", [0x1FA0] = "\u1F68\u0399", [0x1FA1] = "\u1F69\u0399", [0x1FA2] = "\u1F6A\u0399", [0x1FA3] = "\u1F6B\u0399", [0x1FA4] = "\u1F6C\u0399",
        [0x1FA5] = "\u1F6D\u0399", [0x1FA6] = "\u1F6E\u0399", [0x1FA7] = "\u1F6F\u0399", [0x1FA8] = "\u1F68\u0399", [0x1FA9] = "\u1F69\u0399", [0x1FAA] = "\u1F6A\u0399",
        [0x1FAB] = "\u1F6B\u0399", [0x1FAC] = "\u1F6C\u0399", [0x1FAD] = "\u1F6D\u0399", [0x1FAE] = "\u1F6E\u0399", [0x1FAF] = "\u1F6F\u0399", [0x1FB2] = "\u1FBA\u0399",
        [0x1FB3] = "\u0391\u0399", [0x1FB4] = "\u0386\u0399", [0x1FB6] = "\u0391\u0342", [0x1FB7] = "\u0391\u0342\u0399", [0x1FBC] = "\u0391\u0399", [0x1FC2] = "\u1FCA\u0399",
        [0x1FC3] = "\u0397\u0399", [0x1FC4] = "\u0389\u0399", [0x1FC6] = "\u0397\u0342", [0x1FC7] = "\u0397\u0342\u0399", [0x1FCC] = "\u0397\u0399", [0x1FD2] = "\u0399\u0308\u0300",
        [0x1FD3] = "\u0399\u0308\u0301", [0x1FD6] = "\u0399\u0342", [0x1FD7] = "\u0399\u0308\u0342", [0x1FE2] = "\u03A5\u0308\u0300", [0x1FE3] = "\u03A5\u0308\u0301", [0x1FE4] = "\u03A1\u0313",
        [0x1FE6] = "\u03A5\u0342", [0x1FE7] = "\u03A5\u0308\u0342", [0x1FF2] = "\u1FFA\u0399", [0x1FF3] = "\u03A9\u0399", [0x1FF4] = "\u038F\u0399", [0x1FF6] = "\u03A9\u0342",
        [0x1FF7] = "\u03A9\u0342\u0399", [0x1FFC] = "\u03A9\u0399", [0xFB00] = "FF", [0xFB01] = "FI", [0xFB02] = "FL", [0xFB03] = "FFI",
        [0xFB04] = "FFL", [0xFB05] = "ST", [0xFB06] = "ST", [0xFB13] = "\u0544\u0546", [0xFB14] = "\u0544\u0535", [0xFB15] = "\u0544\u053B",
        [0xFB16] = "\u054E\u0546", [0xFB17] = "\u0544\u053D",
    }.ToFrozenDictionary();
}
