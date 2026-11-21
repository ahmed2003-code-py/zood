using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Zawed.Api.Platform.Text;

namespace Zawed.Api.Modules.Leads;

/// <summary>
/// How the demo form's text is cleaned before it is validated (u1-leads-contract.md §2; Java <c>DemoRequestText</c>).
/// Cleaning never makes an invalid value valid by guessing: it removes what a person does not mean (spaces at the ends,
/// runs of spaces, line breaks pasted into a name, the formatting of a phone number) and leaves the rest for validation
/// to accept or refuse.
/// <para>The Java checks are regular expressions over code points (<c>^[‌[^\p{C}]]*$</c>). .NET's engine matches
/// UTF-16 units, where every astral character is two surrogates (category Cs) and would be refused, so the same rule is
/// written here as a loop over code points: <see cref="IsPlainText"/>, <see cref="IsNoteText"/>, <see cref="IsEmail"/>.</para>
/// </summary>
public static partial class DemoRequestText
{
    /// <summary>The Java pattern of a name, kept for the OpenAPI document: no category-C character but the ZWNJ.</summary>
    public const string PlainText = "^[\\u200C[^\\p{C}]]*$";

    /// <summary>The Java pattern of an Ops note: the same, with line breaks and tabs.</summary>
    public const string NoteText = "^[\\t\\n\\r\\u200C[^\\p{C}]]*$";

    /// <summary>The Java pattern of an e-mail address.</summary>
    public const string Email = "[^@\\s\\p{Z}\\p{C}]+@[^@\\s\\p{Z}\\p{C}]+\\.[^@\\s\\p{Z}\\p{C}]+";

    /// <summary>A normalised Egyptian mobile number: 010, 011, 012 or 015, then eight digits.</summary>
    public const string EgyptianMobile = EgyptianContact.Mobile;

    /// <summary>
    /// What validation refuses in a name: every "other" character (Unicode category C): control characters, format
    /// characters (zero-width spaces and joiners, the directional marks, embeddings, overrides and isolates, word joiners,
    /// the byte-order mark, soft hyphens, tags), private-use, unassigned and lone surrogate code points. The one exception
    /// is the zero-width non-joiner (U+200C), which shapes Arabic-script text.
    /// </summary>
    public static bool IsPlainText(string value) => AllRunes(value, r => r.Value == 0x200C || !IsOther(r));

    /// <summary>The same for an Ops note, which keeps its line breaks and tabs.</summary>
    public static bool IsNoteText(string value) => AllRunes(value, r => r.Value is '\t' or '\n' or '\r' or 0x200C || !IsOther(r));

    /// <summary>
    /// An e-mail address (Java <c>@Email(regexp = EMAIL)</c>): one <c>@</c>, a dot in the domain with text on both sides, and no
    /// whitespace (Unicode spaces such as U+00A0 and U+3000 included) or category-C character anywhere.
    /// </summary>
    public static bool IsEmail(string value)
    {
        var at = value.IndexOf('@');
        if (at <= 0 || value.IndexOf('@', at + 1) >= 0)
        {
            return false;
        }
        var domain = value[(at + 1)..];
        var dot = domain.IndexOf('.', 1);
        var dotWithTextAfter = false;
        for (; dot > 0; dot = domain.IndexOf('.', dot + 1))
        {
            if (dot < domain.Length - 1)
            {
                dotWithTextAfter = true;
                break;
            }
        }
        return dotWithTextAfter && AllRunes(value.Remove(at, 1), r => !IsSpaceOrOther(r));
    }

    /// <summary>Trims and collapses every run of whitespace (tabs and line breaks too) to one space; null stays null.</summary>
    public static string? Collapse(string? value)
    {
        if (value is null)
        {
            return null;
        }
        return JavaStrip(Whitespace().Replace(value, " "));
    }

    /// <summary>
    /// An Egyptian mobile number as <c>01XXXXXXXXX</c>, cleaned by the shared rule (<see cref="EgyptianContact.PhoneOf"/>):
    /// Arabic-Indic digits, separators, <c>+20</c> and <c>0020</c>.
    /// </summary>
    public static string? Phone(string? value) => EgyptianContact.PhoneOf(value);

    /// <summary>Trimmed and lower-cased, so a repeat with other capitals is the same contact.</summary>
    public static string? EmailOf(string? value) => value is null ? null : JavaStrip(value).ToLowerInvariant();

    /// <summary>A note keeps its line breaks; only the ends are trimmed. Blank is no note.</summary>
    public static string? Note(string? value)
    {
        if (value is null)
        {
            return null;
        }
        var trimmed = JavaStrip(value);
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>Unicode category C (Cc, Cf, Cs, Co, Cn); a lone surrogate is Cs.</summary>
    private static bool IsOther(Rune rune) => Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
        or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned;

    /// <summary>Java's <c>[\s\p{Z}\p{C}]</c>: ASCII whitespace, separators (Z) and category C.</summary>
    private static bool IsSpaceOrOther(Rune rune) => rune.Value is ' ' or '\t' or '\n' or 0x0B or '\f' or '\r' || IsOther(rune)
        || Rune.GetUnicodeCategory(rune) is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    /// <summary>Every code point satisfies <paramref name="test"/>; a lone surrogate is tested as category Cs.</summary>
    private static bool AllRunes(string value, Func<Rune, bool> test)
    {
        for (var i = 0; i < value.Length;)
        {
            var status = Rune.DecodeFromUtf16(value.AsSpan(i), out var rune, out var consumed);
            if (status != System.Buffers.OperationStatus.Done)
            {
                return false; // a lone surrogate: category Cs
            }
            if (!test(rune))
            {
                return false;
            }
            i += consumed;
        }
        return true;
    }

    /// <summary>Java's <c>String.isBlank()</c>: only <c>Character.isWhitespace</c> characters (a no-break space is not one).</summary>
    public static bool IsBlank(string value) => value.All(IsJavaWhitespace);

    /// <summary>Java's <c>String.strip()</c>: <c>Character.isWhitespace</c> off both ends.</summary>
    private static string JavaStrip(string value)
    {
        int start = 0, end = value.Length;
        while (start < end && IsJavaWhitespace(value[start]))
        {
            start++;
        }
        while (end > start && IsJavaWhitespace(value[end - 1]))
        {
            end--;
        }
        return value[start..end];
    }

    private static bool IsJavaWhitespace(char c) => c switch
    {
        ' ' or ' ' or ' ' => false,
        '\t' or '\n' or '\u000B' or '\f' or '\r' or '\u001C' or '\u001D' or '\u001E' or '\u001F' => true,
        _ => char.GetUnicodeCategory(c) is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator,
    };

    /// <summary>Java's <c>[\s\p{Z}\u0085]+</c>: ASCII whitespace, every Unicode separator, and NEL.</summary>
    [GeneratedRegex("[ \t\n\u000B\f\r\\p{Z}\u0085]+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
