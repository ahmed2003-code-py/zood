using System.Security.Cryptography;
using System.Text;
using Zawed.Api.Platform.Text;

namespace Zawed.Tests.Platform;

/// <summary>
/// Java's text rules, which the port must reproduce where a Java and a .NET instance read the same input during the
/// cutover: <c>Character.isWhitespace</c>, <c>String.strip()</c>, <c>isBlank()</c>, <c>trim()</c>,
/// <c>toUpperCase(Locale.ROOT)</c> and <c>Long.parseLong</c>. The expected values were produced with JDK 25.0.5.
/// </summary>
public sealed class JavaTextTests
{
    /// <summary>Every BMP character for which JDK 25's <c>Character.isWhitespace</c> is true.</summary>
    private static readonly int[] JavaWhitespace =
    [
        0x9, 0xA, 0xB, 0xC, 0xD, 0x1C, 0x1D, 0x1E, 0x1F, 0x20, 0x1680, 0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006,
        0x2008, 0x2009, 0x200A, 0x2028, 0x2029, 0x205F, 0x3000,
    ];

    [Fact]
    public void WhitespaceIsJavasCharacterIsWhitespaceForEveryCharacter()
    {
        var expected = JavaWhitespace.ToHashSet();
        for (var c = 0; c <= 0xFFFF; c++)
        {
            Assert.True(expected.Contains(c) == JavaText.IsWhitespace((char)c), $"U+{c:X4}");
        }
    }

    [Theory]
    // The no-break spaces (U+00A0, U+2007, U+202F), NEL (U+0085) and the BOM are not Java whitespace: strip() keeps them,
    // where .NET's Trim() removes all but the BOM.
    [InlineData("\u00A0abc\u00A0", "\u00A0abc\u00A0")]
    [InlineData("\u2007abc\u202F", "\u2007abc\u202F")]
    [InlineData("\u0085abc\u0085", "\u0085abc\u0085")]
    [InlineData("\uFEFFabc", "\uFEFFabc")]
    // U+001C to U+001F, the line and paragraph separators and the ideographic space are Java whitespace.
    [InlineData("\u001Cabc\u001F", "abc")]
    [InlineData("\u2028 abc\u2029\u3000", "abc")]
    [InlineData(" \t a b \r\n", "a b")]
    [InlineData("", "")]
    public void StripRemovesOnlyJavaWhitespace(string value, string stripped) => Assert.Equal(stripped, JavaText.Strip(value));

    [Fact]
    public void BlankAndTrimFollowJava()
    {
        Assert.True(JavaText.IsBlank(""));
        Assert.True(JavaText.IsBlank(" \t\u2028\u3000"));
        Assert.False(JavaText.IsBlank("\u00A0"));
        Assert.False(JavaText.IsBlank("\u0085"));
        // trim(): every character up to U+0020, nothing above it.
        Assert.Equal("a", JavaText.Trim("\u0000\u0001 a\u001F "));
        Assert.Equal("\u00A0a\u2028", JavaText.Trim("\u00A0a\u2028"));
    }

    [Theory]
    [InlineData("ıd", "ID")] // the dotless i, which .NET's invariant upper case leaves as it is
    [InlineData("straße", "STRASSE")] // full case mapping: one character becomes two
    [InlineData("ﬁle", "FILE")]
    [InlineData("ſ", "S")]
    [InlineData("ŉ", "ʼN")]
    [InlineData("ΐ", "Ι\u0308\u0301")]
    [InlineData("ᾳ", "ΑΙ")]
    [InlineData("i", "I")] // never the Turkish dotted capital
    [InlineData("𐐨", "𐐀")] // outside the BMP
    [InlineData("abc-123", "ABC-123")]
    public void UpperCaseIsJavasRootLocaleUpperCase(string value, string upper) => Assert.Equal(upper, JavaText.ToUpperRoot(value));

    [Fact]
    public void UpperCaseMatchesJavaForEveryCodePoint()
    {
        // SHA-256 of the UTF-8 of new String(Character.toChars(cp)).toUpperCase(Locale.ROOT) + "\n" for every code point
        // but the surrogates, computed with JDK 25.0.5.
        var all = new StringBuilder(4_500_000);
        for (var cp = 0; cp <= 0x10FFFF; cp++)
        {
            if (cp is >= 0xD800 and <= 0xDFFF)
            {
                continue;
            }
            all.Append(JavaText.ToUpperRoot(char.ConvertFromUtf32(cp))).Append('\n');
        }
        Assert.Equal("82b3f90a25cd3b7810fb996c2e4552354cad469104c81f8ae15945b5c6af0621",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(all.ToString()))));
    }

    [Theory]
    [InlineData("123", 123L)]
    [InlineData("١٢٣", 123L)] // any Unicode decimal digit, as Character.digit reads it
    [InlineData("+٤٥", 45L)]
    [InlineData("-１２", -12L)]
    [InlineData("۱۲۳", 123L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    public void ParseLongReadsWhatJavaReads(string value, long parsed)
    {
        Assert.True(JavaText.TryParseLong(value, out var result));
        Assert.Equal(parsed, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData(" 1")]
    [InlineData("1_0")]
    [InlineData("1.0")]
    [InlineData("²")] // a digit value, but not a decimal digit
    [InlineData("𝟏")] // a decimal digit outside the BMP: Long.parseLong reads chars, and a surrogate is no digit
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854775809")]
    [InlineData("99999999999999999999")]
    public void ParseLongRefusesWhatJavaRefuses(string value) => Assert.False(JavaText.TryParseLong(value, out _));
}
