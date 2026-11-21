using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Zawed.Api.Modules.Identity;

/// <summary>
/// Time-based one-time passwords (Java <c>Totp</c>), RFC 6238 with the values every authenticator app supports:
/// HMAC-SHA1, 6 digits, 30-second steps. Tested against the RFC's own vectors.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const long StepSeconds = 30;

    /// <summary>One step either side absorbs clock drift between the phone and the server.</summary>
    public const int Window = 1;

    public const int SecretBytes = 20;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    /// <summary>Java's <c>Math.floorDiv(epochSeconds, 30)</c>.</summary>
    public static long StepAt(long epochSeconds) =>
        epochSeconds >= 0 ? epochSeconds / StepSeconds : (epochSeconds - StepSeconds + 1) / StepSeconds;

    /// <summary>The code for one time step, zero-padded to <see cref="Digits"/>.</summary>
    public static string CodeAt(byte[] secret, long step) => CodeAt(secret, step, Digits, "HmacSHA1");

    public static string CodeAt(byte[] secret, long step, int digits, string algorithm)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var hash = algorithm switch
        {
            "HmacSHA1" => HMACSHA1.HashData(secret, counter),
            "HmacSHA256" => HMACSHA256.HashData(secret, counter),
            "HmacSHA512" => HMACSHA512.HashData(secret, counter),
            _ => throw new ArgumentException("Unknown algorithm " + algorithm),
        };
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | ((hash[offset + 1] & 0xff) << 16) | ((hash[offset + 2] & 0xff) << 8)
            | (hash[offset + 3] & 0xff);
        var modulus = 1;
        for (var i = 0; i < digits; i++)
        {
            modulus *= 10;
        }
        return (binary % modulus).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    /// <summary>
    /// The step a code matches within the window around <paramref name="epochSeconds"/>, or null. Every step in the
    /// window is compared, in constant time, so timing does not reveal which one matched.
    /// </summary>
    public static long? MatchingStep(byte[] secret, string? code, long epochSeconds)
    {
        if (code is null || code.Length != Digits || !code.All(char.IsDigit))
        {
            return null;
        }
        var now = StepAt(epochSeconds);
        long? matched = null;
        for (var step = now - Window; step <= now + Window; step++)
        {
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(CodeAt(secret, step)), Encoding.UTF8.GetBytes(code)))
            {
                matched = step;
            }
        }
        return matched;
    }

    public static string Base32(byte[] bytes)
    {
        var output = new StringBuilder((bytes.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0)
        {
            output.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        }
        return output.ToString();
    }

    /// <summary>The URI an authenticator app reads from the QR code.</summary>
    public static string OtpauthUri(string issuer, string account, byte[] secret) =>
        "otpauth://totp/" + FormEncode(issuer + ":" + account).Replace("+", "%20") + "?secret=" + Base32(secret) + "&issuer="
        + FormEncode(issuer) + "&algorithm=SHA1&digits=" + Digits + "&period=" + StepSeconds;

    /// <summary>Java's <c>URLEncoder.encode(value, UTF_8)</c>: letters, digits and <c>.-*_</c> kept, space as <c>+</c>.</summary>
    private static string FormEncode(string value)
    {
        var output = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '*' or '_')
            {
                output.Append(c);
            }
            else if (c == ' ')
            {
                output.Append('+');
            }
            else
            {
                output.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        return output.ToString();
    }
}
