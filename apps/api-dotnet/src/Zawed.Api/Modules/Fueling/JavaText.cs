using System.Globalization;
using System.Numerics;
using System.Text;

namespace Zawed.Api.Modules.Fueling;

/// <summary>
/// Java's <c>Instant.toString()</c>, for the values the Java engine wrote into alert details as text (<c>deviceAt</c>):
/// seconds always, the fraction in groups of three digits when there is one. (A copy of pricing's private helper: modules
/// do not reach into each other.)
/// </summary>
internal static class JavaInstant
{
    public static string Text(DateTimeOffset at)
    {
        var utc = at.UtcDateTime;
        var fraction = utc.Ticks % TimeSpan.TicksPerSecond * 100; // nanoseconds
        var text = utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        if (fraction != 0)
        {
            text += fraction % 1_000_000 == 0 ? "." + (fraction / 1_000_000).ToString("D3", CultureInfo.InvariantCulture)
                : fraction % 1_000 == 0 ? "." + (fraction / 1_000).ToString("D6", CultureInfo.InvariantCulture)
                : "." + fraction.ToString("D9", CultureInfo.InvariantCulture);
        }
        return text + "Z";
    }
}

/// <summary>
/// A JSON number exactly as the device wrote it, for the two fields of an offline item that Java read as a
/// <c>BigDecimal</c> (<c>volumeMl</c>, <c>unitPricePiastres</c>; B5 contract §3): one item's fraction must not refuse the
/// whole batch. <see cref="Whole"/> is Java's <c>longValueExact</c> (<c>20000.0</c> and <c>2e4</c> are 20000; a real fraction,
/// or a value beyond a <c>long</c>, is not whole), and <see cref="ToString"/> is <c>BigDecimal.toString()</c> of the value
/// Jackson parsed, kept in the rejection alert as <c>sentValue</c>. No floating point: the digits are handled as text.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(SyncNumberConverter))]
public readonly record struct SyncNumber
{
    private readonly bool negative;
    private readonly string digits; // the significand, without sign, point or leading zeros ("0" for zero)
    private readonly int scale; // BigDecimal's scale: the value is digits × 10^-scale

    private SyncNumber(bool negative, string digits, int scale)
    {
        this.negative = negative;
        this.digits = digits;
        this.scale = scale;
    }

    /// <summary>A JSON number literal (RFC 8259 grammar, as the reader already checked it); null when it is not one.</summary>
    public static SyncNumber? Parse(string literal)
    {
        var text = literal.AsSpan();
        var negative = text.StartsWith("-");
        if (negative)
        {
            text = text[1..];
        }
        var e = text.IndexOfAny('e', 'E');
        long exponent = 0;
        if (e >= 0)
        {
            if (!long.TryParse(text[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)
                || exponent is > int.MaxValue / 2 or < int.MinValue / 2)
            {
                return null;
            }
            text = text[..e];
        }
        var point = text.IndexOf('.');
        var whole = point >= 0 ? text[..point] : text;
        var fraction = point >= 0 ? text[(point + 1)..] : [];
        if (whole.Length == 0 || !IsDigits(whole) || !IsDigits(fraction))
        {
            return null;
        }
        var all = (string.Concat(whole, fraction)).TrimStart('0');
        return new SyncNumber(negative, all.Length == 0 ? "0" : all, (int)(fraction.Length - exponent));
    }

    private static bool IsDigits(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The value as a <c>long</c> when it is a whole number that fits one (Java <c>longValueExact</c>), otherwise null.</summary>
    public long? Whole
    {
        get
        {
            if (digits is null)
            {
                return null;
            }
            if (digits == "0")
            {
                return 0;
            }
            var significant = digits.TrimEnd('0');
            var shift = (long)digits.Length - significant.Length - scale; // value = significant × 10^shift
            if (shift < 0 || significant.Length + shift > 19)
            {
                return null;
            }
            var magnitude = BigInteger.Parse(significant, CultureInfo.InvariantCulture) * BigInteger.Pow(10, (int)shift);
            var value = negative ? -magnitude : magnitude;
            return value >= long.MinValue && value <= long.MaxValue ? (long)value : null;
        }
    }

    /// <summary>Java's <c>BigDecimal.toString()</c> of the same value: plain, or scientific when the exponent calls for it.</summary>
    public override string ToString()
    {
        if (digits is null)
        {
            return "";
        }
        var sign = negative && digits != "0" ? "-" : "";
        if (scale == 0)
        {
            return sign + digits;
        }
        var adjusted = -(long)scale + (digits.Length - 1);
        var text = new StringBuilder(sign);
        if (scale > 0 && adjusted >= -6)
        {
            var pad = scale - digits.Length;
            if (pad >= 0)
            {
                text.Append("0.").Append('0', pad).Append(digits);
            }
            else
            {
                text.Append(digits, 0, digits.Length - scale).Append('.').Append(digits, digits.Length - scale, scale);
            }
            return text.ToString();
        }
        text.Append(digits[0]);
        if (digits.Length > 1)
        {
            text.Append('.').Append(digits, 1, digits.Length - 1);
        }
        if (adjusted != 0)
        {
            text.Append('E');
            if (adjusted > 0)
            {
                text.Append('+');
            }
            text.Append(adjusted.ToString(CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }
}

/// <summary>Reads a JSON number token as written; anything else (a string, a boolean) is a malformed request, as in Java.</summary>
internal sealed class SyncNumberConverter : System.Text.Json.Serialization.JsonConverter<SyncNumber>
{
    public override SyncNumber Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType != System.Text.Json.JsonTokenType.Number)
        {
            throw new System.Text.Json.JsonException("Expected a number");
        }
        var raw = reader.HasValueSequence ? System.Buffers.BuffersExtensions.ToArray(reader.ValueSequence) : reader.ValueSpan.ToArray();
        return SyncNumber.Parse(Encoding.ASCII.GetString(raw)) ?? throw new System.Text.Json.JsonException("Not a number");
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, SyncNumber value, System.Text.Json.JsonSerializerOptions options) =>
        writer.WriteRawValue(value.ToString(), skipInputValidation: true);
}
