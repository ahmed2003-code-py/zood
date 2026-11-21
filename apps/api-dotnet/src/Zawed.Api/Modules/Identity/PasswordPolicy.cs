using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Identity;

/// <summary>
/// Length is what makes a password strong; composition rules ("one symbol") mostly produce <c>Password1!</c>. So:
/// 12–128 characters, and not built from the account's own email (contract §6; Java <c>PasswordPolicy</c>).
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    /// <summary>Null when the password meets the policy, else the Java refusal.</summary>
    public static Result? Check(string? password, string? email)
    {
        // Code points, as Java's codePointCount: an emoji is one character, not two.
        var length = password is null ? 0 : password.EnumerateRunes().Count();
        var localPart = email is null ? "" : email[..Math.Max(0, email.IndexOf('@'))].ToLowerInvariant();
        var containsEmail = localPart.Length >= 3 && password is not null
            && password.ToLowerInvariant().Contains(localPart, StringComparison.Ordinal);
        if (length < MinLength || length > MaxLength || containsEmail || (password is not null && IsBlank(password)))
        {
            return Result.Fail(400, "PASSWORD_TOO_WEAK", "The password does not meet the policy", "newPassword",
                new Dictionary<string, object> { ["minLength"] = MinLength, ["maxLength"] = MaxLength });
        }
        return null;
    }

    /// <summary>Java's <c>String.isBlank</c>: every character is <c>Character.isWhitespace</c>, which excludes the no-break spaces.</summary>
    public static bool IsBlank(string value) => value.All(c =>
        c is '\t' or '\n' or '\u000B' or '\f' or '\r' or (>= '\u001C' and <= '\u001F')
        || (char.IsWhiteSpace(c) && c is not ('\u00A0' or '\u2007' or '\u202F' or '\u0085')));
}
