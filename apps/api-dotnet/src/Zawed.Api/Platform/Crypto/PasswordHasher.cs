using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Zawed.Api.Platform.Crypto;

/// <summary>
/// Argon2id with the Java API's parameters (OWASP minimum: 19 MiB, 2 iterations, 1 lane, 16-byte salt,
/// 32-byte hash) and Spring Security's encoded form, so every hash stored by the Java API verifies here
/// and the reverse: <c>$argon2id$v=19$m=19456,t=2,p=1$&lt;salt&gt;$&lt;hash&gt;</c>, base64 without padding.
/// <see cref="Matches"/> reads the parameters from the stored hash, as Spring does. Used for passwords
/// and agent PINs. <see cref="Burn"/> spends a full check on a dummy hash for an unknown or locked account,
/// so timing does not reveal whether an account exists.
/// </summary>
public sealed class PasswordHasher
{
    public const int SaltBytes = 16;
    public const int HashBytes = 32;
    public const int Parallelism = 1;
    public const int MemoryKib = 19 * 1024;
    public const int Iterations = 2;

    private readonly string dummyHash;

    public PasswordHasher() => dummyHash = Hash(SecureTokens.Random(32));

    public string Hash(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Compute(secret, salt, MemoryKib, Iterations, Parallelism, HashBytes);
        return $"$argon2id$v=19$m={MemoryKib},t={Iterations},p={Parallelism}${B64(salt)}${B64(hash)}";
    }

    public bool Matches(string secret, string? encoded)
    {
        if (secret is null || encoded is null)
        {
            return false;
        }
        var parts = encoded.Split('$');
        // "", "argon2id", "v=19", "m=..,t=..,p=..", salt, hash
        if (parts.Length != 6 || parts[1] != "argon2id" || parts[2] != "v=19")
        {
            return false;
        }
        int m = 0, t = 0, p = 0;
        foreach (var pair in parts[3].Split(','))
        {
            var kv = pair.Split('=');
            if (kv.Length != 2 || !int.TryParse(kv[1], out var value))
            {
                return false;
            }
            switch (kv[0])
            {
                case "m": m = value; break;
                case "t": t = value; break;
                case "p": p = value; break;
                default: return false;
            }
        }
        byte[] salt, expected;
        try
        {
            salt = FromB64(parts[4]);
            expected = FromB64(parts[5]);
        }
        catch (FormatException)
        {
            return false;
        }
        if (m <= 0 || t <= 0 || p <= 0 || expected.Length == 0)
        {
            return false;
        }
        var actual = Compute(secret, salt, m, t, p, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Spends the same work as a real check and always fails.</summary>
    public void Burn(string secret) => Matches(secret, dummyHash);

    private static byte[] Compute(string secret, byte[] salt, int memoryKib, int iterations, int parallelism, int length)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(secret))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(length);
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static byte[] FromB64(string value) => Convert.FromBase64String(value.PadRight(value.Length + (4 - value.Length % 4) % 4, '='));
}
