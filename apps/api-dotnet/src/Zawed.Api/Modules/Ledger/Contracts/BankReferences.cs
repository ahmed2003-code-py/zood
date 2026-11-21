using System.Globalization;
using System.Text;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Ledger.Contracts;

/// <summary>
/// Bank transfer references, as people type and paste them (Java <c>ledger.BankReferences</c>). One transfer must be
/// one transfer however its reference was written: a top-up credits once (B3) and a payout pays once (B6), each
/// guarded by a unique index on the <see cref="Key"/> of its reference.
/// </summary>
public static class BankReferences
{
    /// <summary>
    /// The comparison form of a bank reference: NFKC (full-width and compatibility forms), digits of any script folded
    /// to 0-9 (<c>١٢٣</c> is <c>123</c>), upper case, and then only A-Z and 0-9 kept: spaces, dashes, non-breaking
    /// spaces and invisible direction marks copied from Arabic interfaces all vanish. <c>" ft-١٢٣ "</c>,
    /// <c>"FT 123"</c> and <c>"FT‏123"</c> are the same transfer. The database checks the same shape (V6_1, V9).
    /// </summary>
    public static string Key(string bankReference)
    {
        var normalized = bankReference.Normalize(NormalizationForm.FormKC);
        var key = new StringBuilder(normalized.Length);
        for (var i = 0; i < normalized.Length; i += char.IsSurrogatePair(normalized, i) ? 2 : 1)
        {
            var rune = Rune.GetRuneAt(normalized, i);
            // Java Character.isDigit is the Nd category, and Character.digit(cp, 10) its decimal value.
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber)
            {
                key.Append((char)('0' + CharUnicodeInfo.GetDecimalDigitValue(normalized, i)));
                continue;
            }
            var upper = Rune.ToUpperInvariant(rune).Value;
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
    public static string Clean(string? bankReference)
    {
        var reference = Sql.Clean(bankReference);
        if (reference is null || Key(reference).Length == 0)
        {
            throw new ApiException(400, "INVALID_BANK_REFERENCE", "A bank reference is required", "bankReference");
        }
        return reference;
    }
}
