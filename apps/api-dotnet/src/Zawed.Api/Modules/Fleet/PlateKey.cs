using System.Text;
using System.Text.RegularExpressions;

namespace Zawed.Api.Modules.Fleet;

/// <summary>
/// The comparison form of a plate, used only to find duplicates (Java <c>PlateKey</c>). The plate itself is stored and
/// shown exactly as registered (approved decision: never transliterated).
/// <para>Egyptian plates mix Arabic letters and digits, written with spaces, dashes or none, in Arabic or Western digits.
/// Two spellings of the same plate must collide, so the key keeps only letters and digits, turns Eastern Arabic and
/// Persian digits into Western ones, and folds the Arabic letter variants people type interchangeably (alef forms, teh
/// marbuta, alef maksura). Since V14 the digit group comes first for plates of the Egyptian shape (see
/// <see cref="Canonical"/>); the database applies the same rule to existing keys (<c>plate_key_canonical</c>).</para>
/// <para>Code point by code point, as Java: NFKC, then <c>Character.isLetterOrDigit</c> (the general categories L* and
/// Nd, which <see cref="Rune.IsLetterOrDigit"/> tests) and <c>Character.toUpperCase</c> (locale-independent).</para>
/// </summary>
public static partial class PlateKey
{
    /// <summary>U+0640, the Arabic tatweel: it stretches a letter and carries no meaning of its own.</summary>
    public const string Tatweel = "ـ";

    public static string Of(string plate)
    {
        var normalized = plate.Normalize(NormalizationForm.FormKC);
        var key = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
        {
            var c = Fold(rune);
            if (Rune.IsLetterOrDigit(c))
            {
                key.Append(Rune.ToUpperInvariant(c).ToString());
            }
        }
        return Canonical(key.ToString());
    }

    /// <summary>
    /// What a search compares (QA round 1): the key without tatweels, on the query and on the stored key alike
    /// (<see cref="Vehicles.ListAsync"/>). Identity is not changed: <see cref="Of"/> still keeps a tatweel, so the keys of
    /// stored plates and the unique rule on them stay exactly as V14 left them.
    /// </summary>
    public static string SearchKey(string query) => Canonical(Of(query).Replace(Tatweel, "", StringComparison.Ordinal));

    /// <summary>
    /// The Egyptian shape (one digit group and one letter group) in one order, digits first, so the order a plate was
    /// typed in does not matter (V14). Any other shape stays as it is: two different plates must never share a key.
    /// </summary>
    public static string Canonical(string key)
    {
        if (DigitsThenOther().IsMatch(key) || OtherThenDigits().IsMatch(key))
        {
            return NotDigits().Replace(key, "") + Digits().Replace(key, "");
        }
        return key;
    }

    private static Rune Fold(Rune rune)
    {
        var cp = rune.Value;
        if (cp is >= 0x0660 and <= 0x0669)
        {
            return new Rune('0' + (cp - 0x0660)); // Arabic-Indic digits
        }
        if (cp is >= 0x06F0 and <= 0x06F9)
        {
            return new Rune('0' + (cp - 0x06F0)); // Extended Arabic-Indic (Persian) digits
        }
        return cp switch
        {
            0x0623 or 0x0625 or 0x0622 or 0x0671 => new Rune(0x0627), // أ إ آ ٱ -> ا
            0x0629 => new Rune(0x0647), // ة -> ه
            0x0649 => new Rune(0x064A), // ى -> ي
            _ => rune,
        };
    }

    [GeneratedRegex(@"\A[0-9]+[^0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex DigitsThenOther();

    [GeneratedRegex(@"\A[^0-9]+[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex OtherThenDigits();

    [GeneratedRegex("[^0-9]", RegexOptions.CultureInvariant)]
    private static partial Regex NotDigits();

    [GeneratedRegex("[0-9]", RegexOptions.CultureInvariant)]
    private static partial Regex Digits();
}
