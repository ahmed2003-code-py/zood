using System.Text;
using System.Text.RegularExpressions;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Platform.Text;

/// <summary>
/// Egyptian contact numbers as people type them, cleaned to one stored form (QA round 1; Java
/// <c>platform.text.EgyptianContact</c>). Cleaning never makes an invalid value valid by guessing: it turns
/// Arabic-Indic and Persian digits into ASCII, removes the separators people type (spaces, dashes, dots,
/// brackets) and the international prefix, and leaves the rest for validation (<see cref="Phone"/>,
/// <see cref="Mobile"/>, <see cref="TaxIdRule"/>, <see cref="CommercialRegistrationRule"/>) to accept or refuse.
/// </summary>
public static partial class EgyptianContact
{
    /// <summary>A normalised Egyptian mobile number: 010, 011, 012 or 015, then eight digits.</summary>
    public const string Mobile = "^01[0125][0-9]{8}$";

    /// <summary>
    /// A normalised Egyptian mobile or landline. Landlines: Cairo and Giza <c>02</c> and eight digits, Alexandria
    /// <c>03</c> and seven, every other area code (<c>013</c>, <c>015</c>, <c>040</c> to <c>097</c>) three digits and seven.
    /// </summary>
    public const string Phone = "^(01[0125][0-9]{8}|02[0-9]{8}|03[0-9]{7}|01[35][0-9]{7}|0[4-9][0-9]{8})$";

    /// <summary>The Egyptian tax registration number, stored as nine digits (typed <c>xxx-xxx-xxx</c> or not).</summary>
    public const string TaxIdRule = "^[0-9]{9}$";

    /// <summary>A commercial registration number, stored as its digits: 3 to 12 (registries number per office).</summary>
    public const string CommercialRegistrationRule = "^[0-9]{3,12}$";

    /// <summary>
    /// A phone number as <c>0…</c>: <c>+20</c>, <c>0020</c>, or <c>20</c> before a twelve-digit mobile become the leading
    /// <c>0</c>, and a ten-digit mobile without its zero gets it back. Anything else is returned without
    /// separators, for validation to refuse. Null stays null.
    /// </summary>
    public static string? PhoneOf(string? value)
    {
        if (value is null)
        {
            return null;
        }
        var compact = Compact(value);
        if (compact.StartsWith("+20", StringComparison.Ordinal))
        {
            compact = "0" + compact[3..];
        }
        else if (compact.StartsWith("0020", StringComparison.Ordinal))
        {
            compact = "0" + compact[4..];
        }
        else if (compact.Length == 12 && compact.StartsWith("201", StringComparison.Ordinal))
        {
            compact = "0" + compact[2..];
        }
        else if (compact.Length == 10 && compact.StartsWith('1'))
        {
            compact = "0" + compact;
        }
        return compact;
    }

    /// <summary>A tax registration number without its separators. Null stays null.</summary>
    public static string? TaxIdOf(string? value) => value is null ? null : Compact(value);

    /// <summary>A commercial registration without its separators. Null stays null.</summary>
    public static string? CommercialRegistrationOf(string? value) => value is null ? null : Compact(value);

    /// <summary>
    /// For an optional field: nothing typed is no value. Blank as Java's <c>String.isBlank</c> means it
    /// (<c>Character.isWhitespace</c>: the no-break spaces are not blank, U+001C to U+001F are).
    /// </summary>
    public static string? OrNull(string? value) => value is null || value.All(Sql.IsJavaWhitespace) ? null : value;

    /// <summary>
    /// Whether the whole value matches the rule, as Java's <c>String.matches</c>: a match that stops short (.NET's
    /// <c>$</c> also matches before a final newline) is no match.
    /// </summary>
    public static bool Matches(string value, string rule)
    {
        var match = Regex.Match(value, rule, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return match.Success && match.Index == 0 && match.Length == value.Length;
    }

    /// <summary>
    /// Cleans and judges the contact numbers of one request, against the stored row when there is one (QA round 1
    /// review). A value sent back exactly as stored is kept as it is, even one written before these rules: a
    /// full-body edit of an old row must not fail on a field nobody touched. Every other value is cleaned and must
    /// match; the failures are collected and thrown together.
    /// </summary>
    /// <param name="prefix">put before each field name, such as <c>company.</c> for a nested body</param>
    public sealed class Check(string prefix)
    {
        private readonly List<FieldIssue> issues = [];

        /// <summary>The failures so far, for a service that answers with a <see cref="Result"/> rather than throwing.</summary>
        public IReadOnlyList<FieldIssue> Issues => issues;

        public string? Phone(string field, string? typed, string? stored) => Judge(field, typed, stored, PhoneOf, EgyptianContact.Phone);

        public string? Mobile(string field, string? typed, string? stored) => Judge(field, typed, stored, PhoneOf, EgyptianContact.Mobile);

        public string? TaxId(string field, string? typed, string? stored) => Judge(field, typed, stored, TaxIdOf, TaxIdRule);

        public string? CommercialRegistration(string field, string? typed, string? stored) =>
            Judge(field, typed, stored, CommercialRegistrationOf, CommercialRegistrationRule);

        /// <summary>Throws 400 VALIDATION_FAILED naming every field that failed, if any did.</summary>
        public void OrThrow()
        {
            if (issues.Count > 0)
            {
                throw new FieldsInvalidException(issues);
            }
        }

        private string? Judge(string field, string? typed, string? stored, Func<string?, string?> cleaner, string rule)
        {
            var asTyped = Sql.Clean(typed);
            if (asTyped is null)
            {
                return null;
            }
            if (asTyped == stored)
            {
                return stored;
            }
            var cleaned = OrNull(cleaner(asTyped));
            if (cleaned is null)
            {
                return null;
            }
            if (!Matches(cleaned, rule))
            {
                issues.Add(new FieldIssue(prefix + field, "PATTERN", $"must match \"{rule}\""));
                return null;
            }
            return cleaned;
        }
    }

    private static string Compact(string value)
    {
        var digits = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            digits.Append(c switch
            {
                >= '٠' and <= '٩' => (char)('0' + (c - '٠')),
                >= '۰' and <= '۹' => (char)('0' + (c - '۰')),
                _ => c,
            });
        }
        return Separators().Replace(digits.ToString(), "");
    }

    /// <summary>Separators people type inside a number (Java: <c>[\s\p{Z}\-.()‐-―]+</c>, where <c>\s</c> is ASCII).</summary>
    [GeneratedRegex(@"[ \t\n\u000B\f\r\p{Z}\-.()‐-―]+", RegexOptions.CultureInvariant)]
    private static partial Regex Separators();
}
