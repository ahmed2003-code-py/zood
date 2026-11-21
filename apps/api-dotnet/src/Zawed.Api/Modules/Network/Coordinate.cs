using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Network;

/// <summary>
/// A latitude or longitude (<c>numeric(9, 6)</c>, Java <c>BigDecimal</c>), carried as its exact decimal text: no
/// <c>double</c> or <c>decimal</c> anywhere (invariant 1, <c>ArchitectureTests</c>). Read from a JSON number (or a
/// numeric string, as Jackson coerces one), sent to PostgreSQL as text cast to <c>numeric</c>, read back as
/// <c>latitude::text</c> and written as a raw JSON number: PostgreSQL's text of a <c>numeric(9, 6)</c> is what Jackson
/// writes for the <c>BigDecimal</c> JDBC returns (<c>30.044400</c>, six decimals always).
/// </summary>
[JsonConverter(typeof(CoordinateJson))]
public readonly record struct Coordinate
{
    private Coordinate(string text, BigInteger unscaled, long scale)
    {
        Text = text;
        Unscaled = unscaled;
        Scale = scale;
    }

    /// <summary>The number as written: what PostgreSQL parses and what the JSON carries.</summary>
    public string Text { get; }

    /// <summary>The value is <c>Unscaled × 10^-Scale</c>, as a <c>BigDecimal</c>.</summary>
    public BigInteger Unscaled { get; }

    public long Scale { get; }

    public override string ToString() => Text;

    /// <summary>A JSON number literal (<c>-12.5</c>, <c>3E+1</c>), or null when it is not one.</summary>
    public static Coordinate? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 400)
        {
            return null;
        }
        var i = 0;
        var negative = false;
        if (text[i] is '-' or '+')
        {
            negative = text[i] == '-';
            i++;
        }
        var digits = new StringBuilder();
        long scale = 0;
        var sawDigit = false;
        for (; i < text.Length && char.IsAsciiDigit(text[i]); i++)
        {
            digits.Append(text[i]);
            sawDigit = true;
        }
        if (i < text.Length && text[i] == '.')
        {
            for (i++; i < text.Length && char.IsAsciiDigit(text[i]); i++)
            {
                digits.Append(text[i]);
                scale++;
                sawDigit = true;
            }
        }
        if (!sawDigit)
        {
            return null;
        }
        if (i < text.Length && text[i] is 'e' or 'E')
        {
            if (!long.TryParse(text.AsSpan(i + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var exponent)
                || Math.Abs(exponent) > 100_000)
            {
                return null;
            }
            scale -= exponent;
            i = text.Length;
        }
        if (i != text.Length)
        {
            return null;
        }
        var unscaled = BigInteger.Parse(digits.ToString(), CultureInfo.InvariantCulture);
        return new Coordinate(text, negative ? -unscaled : unscaled, scale);
    }

    /// <summary>Compared with a whole number, as <c>BigDecimal.compareTo</c> (exact: the text and the exponent are bounded).</summary>
    public int CompareTo(long bound) => Scale >= 0
        ? Unscaled.CompareTo(bound * BigInteger.Pow(10, (int)Scale))
        : (Unscaled * BigInteger.Pow(10, (int)-Scale)).CompareTo(bound);

    /// <summary>The digits before the point and after it, trailing zeros removed (Hibernate's <c>DigitsValidator</c>).</summary>
    public (long Integer, long Fraction) Digits()
    {
        if (Unscaled.IsZero)
        {
            return (1, 0); // BigDecimal.ZERO.stripTrailingZeros(): precision 1, scale 0
        }
        var unscaled = BigInteger.Abs(Unscaled);
        var scale = Scale;
        while (unscaled % 10 == 0)
        {
            unscaled /= 10;
            scale--;
        }
        long precision = unscaled.ToString(CultureInfo.InvariantCulture).Length;
        return (precision - scale, Math.Max(0, scale));
    }

}

/// <summary>A JSON number (or a numeric string) to a <see cref="Coordinate"/>, and back as the same raw number.</summary>
public sealed class CoordinateJson : JsonConverter<Coordinate>
{
    public override Coordinate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.TokenType switch
        {
            JsonTokenType.Number => reader.HasValueSequence ? Encoding.UTF8.GetString(System.Buffers.BuffersExtensions.ToArray(reader.ValueSequence)) : Encoding.UTF8.GetString(reader.ValueSpan),
            JsonTokenType.String => reader.GetString()?.Trim(),
            _ => null,
        };
        return Coordinate.Parse(text) ?? throw new JsonException("Not a number");
    }

    public override void Write(Utf8JsonWriter writer, Coordinate value, JsonSerializerOptions options) => writer.WriteRawValue(value.Text);
}

/// <summary>At least <c>min</c> (Jakarta <c>@DecimalMin</c>, inclusive); null is valid.</summary>
public sealed class DecimalMinAttribute(long min)
    : ValidationAttribute(Validation.Encode("DECIMAL_MIN", $"must be greater than or equal to {min}"))
{
    public override bool IsValid(object? value) => value is not Coordinate c || c.CompareTo(min) >= 0;
}

/// <summary>At most <c>max</c> (Jakarta <c>@DecimalMax</c>, inclusive); null is valid.</summary>
public sealed class DecimalMaxAttribute(long max)
    : ValidationAttribute(Validation.Encode("DECIMAL_MAX", $"must be less than or equal to {max}"))
{
    public override bool IsValid(object? value) => value is not Coordinate c || c.CompareTo(max) <= 0;
}

/// <summary>At most so many digits before and after the point (Jakarta <c>@Digits</c>); null is valid.</summary>
public sealed class DigitsAttribute(int integer, int fraction)
    : ValidationAttribute(Validation.Encode("DIGITS", $"numeric value out of bounds (<{integer} digits>.<{fraction} digits> expected)"))
{
    public override bool IsValid(object? value)
    {
        if (value is not Coordinate c)
        {
            return true;
        }
        var (whole, part) = c.Digits();
        return whole <= integer && part <= fraction;
    }
}
