using System.Security.Cryptography;
using System.Text;

namespace Zawed.Api.Platform.Crypto;

/// <summary>
/// Random tokens and their stored form (Java <c>SecureTokens</c>). A token handed to a client (refresh
/// token, MFA challenge, temporary password) is random and long, so it is stored as a plain SHA-256 hex.
/// </summary>
public static class SecureTokens
{
    private const string PasswordAlphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    /// <summary><paramref name="bytes"/> random bytes, URL-safe base64 without padding.</summary>
    public static string Random(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string TemporaryPassword(int length) => RandomNumberGenerator.GetString(PasswordAlphabet, length);

    public static string Code(int length) => RandomNumberGenerator.GetString(CodeAlphabet, length);

    public static string Digits(int length) => RandomNumberGenerator.GetString("0123456789", length);

    public static string Sha256Hex(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>Compares secrets without leaking, through timing, how many leading characters match.</summary>
    public static bool ConstantTimeEquals(string? a, string? b) =>
        a is not null && b is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
