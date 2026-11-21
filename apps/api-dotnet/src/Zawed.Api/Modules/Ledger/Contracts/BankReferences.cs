using System.Globalization;
using System.Text;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Ledger.Contracts;

/// <summary>
/// Bank transfer references, as people type and paste them (Java <c>ledger.BankReferences</c>). One transfer must be
/// one transfer however its reference was written: a top-up credits once (B3) and a payout pays once (B6), each
/// guarded by a unique index on the <see cref="KeyAsync"/> of its reference.
/// <para>The API runs with <c>InvariantGlobalization</c>, where .NET has no Unicode normalisation, so NFKC is
/// PostgreSQL's <c>normalize(text, NFKC)</c> (the function V6_1 rebuilt the keys with; PostgreSQL 18 and Java 25 both
/// implement Unicode 16). The rest of the key is folded here exactly as Java folds it.</para>
/// </summary>
public static class BankReferences
{
    /// <summary>
    /// The comparison form of a bank reference: NFKC (full-width and compatibility forms), digits of any script folded
    /// to 0-9 (<c>١٢٣</c> is <c>123</c>), upper case, and then only A-Z and 0-9 kept: spaces, dashes, non-breaking
    /// spaces and invisible direction marks copied from Arabic interfaces all vanish. <c>" ft-١٢٣ "</c>,
    /// <c>"FT 123"</c> and <c>"FT‏123"</c> are the same transfer. The database checks the same shape (V6_1, V9).
    /// </summary>
    /// <remarks>
    /// A reference PostgreSQL cannot hold as text (a NUL character, or a lone surrogate, which has no UTF-8 form) is
    /// refused before the query, as <c>400 INVALID_BANK_REFERENCE</c>: <c>normalize</c> would fail with 22021 and the
    /// request would answer 500. Beyond Java, which accepts the NUL and then fails at its insert.
    /// </remarks>
    public static async Task<string> KeyAsync(DbTx tx, string bankReference, CancellationToken ct = default) =>
        Storable(bankReference)
            ? Fold(await tx.QuerySingleAsync<string>("select normalize(@reference, NFKC)", new { reference = bankReference }, ct))
            : throw Invalid();

    /// <summary>Whether PostgreSQL can store the text: no U+0000, and every surrogate in a pair.</summary>
    private static bool Storable(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsSurrogatePair(text, i))
            {
                i++;
            }
            else if (text[i] == '\0' || char.IsSurrogate(text[i]))
            {
                return false;
            }
        }
        return true;
    }

    private static ApiException Invalid() => new(400, "INVALID_BANK_REFERENCE", "A bank reference is required", "bankReference");

    /// <summary>
    /// The key of a reference that is already in NFKC: each decimal digit (Java <c>Character.isDigit</c>, the Nd
    /// category) becomes its ASCII digit, and a letter is kept when Java's <c>Character.toUpperCase</c> makes it A-Z.
    /// After NFKC the only letters outside ASCII whose upper case is A-Z is the dotless i (ı → I); the long s and the
    /// Kelvin sign were folded by NFKC itself.
    /// </summary>
    public static string Fold(string nfkc)
    {
        var key = new StringBuilder(nfkc.Length);
        for (var i = 0; i < nfkc.Length; i += char.IsSurrogatePair(nfkc, i) ? 2 : 1)
        {
            var rune = Rune.GetRuneAt(nfkc, i);
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber)
            {
                key.Append((char)('0' + CharUnicodeInfo.GetDecimalDigitValue(nfkc, i)));
                continue;
            }
            var upper = rune.Value switch
            {
                >= 'a' and <= 'z' => rune.Value - ('a' - 'A'),
                'ı' => 'I',
                'ſ' => 'S',
                _ => rune.Value,
            };
            if (upper is >= 'A' and <= 'Z')
            {
                key.Append((char)upper);
            }
        }
        return key.ToString();
    }

    /// <summary>
    /// The reference as it is stored for display: trimmed, inner whitespace collapsed. <c>400 INVALID_BANK_REFERENCE</c>
    /// when nothing is left to compare.
    /// </summary>
    public static async Task<string> CleanAsync(DbTx tx, string? bankReference, CancellationToken ct = default)
    {
        var reference = Sql.Clean(bankReference);
        if (reference is null || (await KeyAsync(tx, reference, ct)).Length == 0)
        {
            throw Invalid();
        }
        return reference;
    }
}
