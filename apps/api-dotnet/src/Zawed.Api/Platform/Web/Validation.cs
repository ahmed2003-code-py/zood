using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace Zawed.Api.Platform.Web;

/// <summary>
/// Validation attributes named like the Jakarta Bean Validation constraints the Java DTOs use, so a
/// failure carries the same stable code (<c>NotBlank</c> → <c>NOT_BLANK</c>, <c>Size</c> → <c>SIZE</c>). The code
/// travels in the error message as <c>CODE|message</c> and <see cref="ModelStateErrors"/> splits it.
/// Use these on request DTOs instead of the plain DataAnnotations ones (<c>[Required]</c> would answer
/// <c>INVALID</c>). Messages are Hibernate Validator's English defaults: a fallback, never UI copy.
/// </summary>
public static class Validation
{
    public static string Encode(string code, string message) => code + "|" + message;

    /// <summary>True for a message written by one of these attributes (<c>CODE|message</c>), false for a binder's own.</summary>
    public static bool IsEncoded(string? message) =>
        message is { Length: > 0 } && message.IndexOf('|') is > 0 and var bar
            && message[..bar].All(c => c is >= 'A' and <= 'Z' or '_');

    public static (string Code, string? Message) Decode(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded))
        {
            return ("INVALID", null);
        }
        var bar = encoded.IndexOf('|');
        return bar <= 0 ? ("INVALID", encoded) : (encoded[..bar], encoded[(bar + 1)..]);
    }
}

/// <summary>Must be present (Jakarta <c>@NotNull</c>).</summary>
public sealed class NotNullAttribute : ValidationAttribute
{
    public NotNullAttribute() : base(Validation.Encode("NOT_NULL", "must not be null")) { }
    public override bool IsValid(object? value) => value is not null;
}

/// <summary>Must contain a non-whitespace character (Jakarta <c>@NotBlank</c>).</summary>
public sealed class NotBlankAttribute : ValidationAttribute
{
    public NotBlankAttribute() : base(Validation.Encode("NOT_BLANK", "must not be blank")) { }
    public override bool IsValid(object? value) => value is string s && !string.IsNullOrWhiteSpace(s);
}

/// <summary>Must not be empty (Jakarta <c>@NotEmpty</c>): a string or a collection.</summary>
public sealed class NotEmptyAttribute : ValidationAttribute
{
    public NotEmptyAttribute() : base(Validation.Encode("NOT_EMPTY", "must not be empty")) { }
    public override bool IsValid(object? value) => value switch
    {
        string s => s.Length > 0,
        System.Collections.ICollection c => c.Count > 0,
        System.Collections.IEnumerable e => e.GetEnumerator().MoveNext(),
        _ => false,
    };
}

/// <summary>Length (string) or count (collection) within bounds; null is valid (Jakarta <c>@Size</c>).</summary>
public sealed class SizeAttribute : ValidationAttribute
{
    public SizeAttribute(int min = 0, int max = int.MaxValue)
        : base(Validation.Encode("SIZE", $"size must be between {min} and {max}"))
    {
        Min = min;
        Max = max;
    }

    public int Min { get; }
    public int Max { get; }

    public override bool IsValid(object? value)
    {
        var length = value switch
        {
            null => (int?)null,
            string s => s.Length,
            System.Collections.ICollection c => c.Count,
            _ => null,
        };
        return length is null || (length >= Min && length <= Max);
    }
}

/// <summary>A whole number at least <c>value</c>; null is valid (Jakarta <c>@Min</c>).</summary>
public sealed class MinAttribute(long min) : ValidationAttribute(Validation.Encode("MIN", $"must be greater than or equal to {min}"))
{
    public override bool IsValid(object? value) => value is null || Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) >= min;
}

/// <summary>A whole number at most <c>value</c>; null is valid (Jakarta <c>@Max</c>).</summary>
public sealed class MaxAttribute(long max) : ValidationAttribute(Validation.Encode("MAX", $"must be less than or equal to {max}"))
{
    public override bool IsValid(object? value) => value is null || Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) <= max;
}

/// <summary>Strictly positive; null is valid (Jakarta <c>@Positive</c>).</summary>
public sealed class PositiveAttribute : ValidationAttribute
{
    public PositiveAttribute() : base(Validation.Encode("POSITIVE", "must be greater than 0")) { }
    public override bool IsValid(object? value) => value is null || Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture) > 0;
}

/// <summary>Zero or more; null is valid (Jakarta <c>@PositiveOrZero</c>).</summary>
public sealed class PositiveOrZeroAttribute : ValidationAttribute
{
    public PositiveOrZeroAttribute() : base(Validation.Encode("POSITIVE_OR_ZERO", "must be greater than or equal to 0")) { }
    public override bool IsValid(object? value) => value is null || Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture) >= 0;
}

/// <summary>The whole string matches the expression; null is valid (Jakarta <c>@Pattern</c>).</summary>
// The message goes through string.Format (ValidationAttribute.FormatErrorMessage), so braces in the expression
// ("[0-9]{8}") are escaped; unescaped, a failed match threw FormatException and answered 500 instead of 400.
public sealed class PatternAttribute(string regexp)
    : ValidationAttribute(Validation.Encode("PATTERN", $"must match \"{regexp.Replace("{", "{{").Replace("}", "}}")}\""))
{
    private readonly Regex expression = new("^(?:" + regexp + ")$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public override bool IsValid(object? value) => value is null || (value is string s && expression.IsMatch(s));

    /// <summary>The message as written: an expression's quantifiers (<c>{8}</c>) are not format placeholders.</summary>
    public override string FormatErrorMessage(string name) => ErrorMessageString;
}

/// <summary>A well-formed e-mail address; null is valid (Jakarta <c>@Email</c>, Hibernate's rule simplified).</summary>
public sealed class EmailAttribute : ValidationAttribute
{
    private static readonly Regex Shape = new(@"^[^@\s]+@[^@\s]+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public EmailAttribute() : base(Validation.Encode("EMAIL", "must be a well-formed email address")) { }
    public override bool IsValid(object? value) => value is null || (value is string s && Shape.IsMatch(s));
}
