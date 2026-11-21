namespace Zawed.Domain;

/// <summary>
/// Money and volume arithmetic on integer minor units (Java <c>platform.money.MoneyMath</c>). No floating point is
/// used for any financial value (PRD §2.3).
/// <list type="bullet">
/// <item>Money is integer piastres (1 EGP = 100 piastres).</item>
/// <item>Fuel volume is integer millilitres (1 L = 1000 mL).</item>
/// <item>Rounding is half away from zero, to the nearest unit.</item>
/// </list>
/// The server-side twin of <c>apps/web/src/domain/money.ts</c>: the same inputs give the same results on both sides,
/// which <c>MoneyMathTests</c> checks with the web's own vectors. Overflow is an <see cref="ArithmeticException"/>
/// (<see cref="OverflowException"/> is one), as Java's <c>Math.*Exact</c> throws.
/// </summary>
public static class MoneyMath
{
    public const long PiastresPerPound = 100;
    public const long MlPerLitre = 1000;

    /// <summary>
    /// Largest integer a JavaScript client represents exactly (2^53 - 1). Amounts travel as JSON numbers, so a value
    /// beyond this bound could not be shown or sent back unchanged.
    /// </summary>
    public const long MaxSafe = 9_007_199_254_740_991L;

    /// <summary>Rejects a value that a JavaScript client could not represent exactly.</summary>
    public static long RequireSafe(long value, string name)
    {
        if (value > MaxSafe || value < -MaxSafe)
        {
            throw new ArithmeticException($"{name} is outside the safe integer range: {value}");
        }
        return value;
    }

    /// <summary>Integer division rounded half away from zero.</summary>
    public static long DivRoundHalfUp(long numerator, long denominator)
    {
        if (denominator <= 0)
        {
            throw new ArgumentException("denominator must be positive", nameof(denominator));
        }
        var magnitude = Math.Abs(numerator); // OverflowException for long.MinValue, as Math.absExact
        var quotient = checked((2L * magnitude) + denominator) / checked(2L * denominator);
        return numerator < 0 ? -quotient : quotient;
    }

    /// <summary>Amount in piastres for a dispensed volume at a unit price (piastres per litre).</summary>
    public static long AmountForVolume(long volumeMl, long unitPricePiastres) =>
        DivRoundHalfUp(checked(volumeMl * unitPricePiastres), MlPerLitre);

    /// <summary>Volume in millilitres that an amount buys at a unit price (piastres per litre).</summary>
    public static long VolumeForAmount(long amountPiastres, long unitPricePiastres) =>
        DivRoundHalfUp(checked(amountPiastres * MlPerLitre), unitPricePiastres);

    /// <summary>
    /// Parses a user-entered decimal string ("42.5", "1,200.75") into integer minor units at the given scale (2 for
    /// piastres, 3 for millilitres). Null for invalid input, a negative number, too many decimals, or a value outside
    /// the safe range. Separators are exactly what JavaScript's <c>[,\s]</c> matches in <c>money.ts</c>: a comma plus
    /// ECMAScript white space (which includes U+FEFF and excludes U+0085); digits are ASCII only.
    /// </summary>
    public static long? ParseDecimal(string? input, int scale)
    {
        if (input is null || scale < 0)
        {
            return null;
        }
        var normalized = new System.Text.StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (!IsSeparator(c))
            {
                normalized.Append(c);
            }
        }
        var text = normalized.ToString();
        var point = text.IndexOf('.');
        var whole = point < 0 ? text : text[..point];
        var fraction = point < 0 ? "" : text[(point + 1)..];
        // [0-9]+(\.[0-9]*)?
        if (whole.Length == 0 || !AllDigits(whole) || !AllDigits(fraction))
        {
            return null;
        }
        if (fraction.Length > scale)
        {
            return null;
        }
        var digits = (whole + fraction + new string('0', scale - fraction.Length)).TrimStart('0');
        if (digits.Length == 0)
        {
            return 0;
        }
        // MaxSafe has 16 digits: anything longer is beyond it (and could overflow a long).
        if (digits.Length > 16)
        {
            return null;
        }
        var minor = long.Parse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
        return minor > MaxSafe ? null : minor;
    }

    public static long PoundsToPiastres(long pounds) => checked(pounds * PiastresPerPound);

    public static long LitresToMl(long litres) => checked(litres * MlPerLitre);

    private static bool AllDigits(string value)
    {
        foreach (var c in value)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>A comma, or ECMAScript <c>\s</c>: WhiteSpace and LineTerminator.</summary>
    private static bool IsSeparator(char c) => c switch
    {
        ',' or '\t' or '\n' or '\u000B' or '\f' or '\r' or ' ' or '\u00A0' or '\u1680' or '\u2028' or '\u2029' or '\u202F'
            or '\u205F' or '\u3000' or '\uFEFF' => true,
        >= '\u2000' and <= '\u200A' => true,
        _ => false,
    };
}
