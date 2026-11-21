using System.Text;
using Zawed.Api.Platform.Crypto;
using Zawed.Tests.Support;

namespace Zawed.Tests.Platform;

/// <summary>
/// The crypto reads and writes what the Java API reads and writes (ADR 0029): the golden values were produced
/// by the Java algorithms (<c>Fixtures/Golden.java</c>), and the behaviour of the Java <c>DataEncryptorTests</c>
/// and <c>LookupHasherTests</c> holds.
/// </summary>
public sealed class CryptoTests
{
    private static readonly Dictionary<string, string> Golden = Fixtures.Text("java-golden-vectors.properties")
        .Split('\n').Where(l => l.Contains('=')).ToDictionary(l => l[..l.IndexOf('=')], l => l[(l.IndexOf('=') + 1)..]);

    private static readonly byte[] Key1 = Convert.FromBase64String("bG9jYWwtb25seS1kYXRhLWtleS1ub3QtZm9yLXByb2Q=");
    private static readonly byte[] Key200 = Convert.FromBase64String(Golden["aes.key200"]);
    private static readonly byte[] Binding = Encoding.UTF8.GetBytes("user:0193a5b2-7c41-7000-8000-000000000001");

    [Fact]
    public void APasswordHashedByTheJavaApiVerifies()
    {
        var hasher = new PasswordHasher();
        Assert.True(hasher.Matches(Golden["argon2.password"], Golden["argon2.hash"]));
        Assert.False(hasher.Matches(Golden["argon2.password"] + "x", Golden["argon2.hash"]));
    }

    [Fact]
    public void AHashWrittenHereHasSpringsFormatAndParameters()
    {
        var hash = new PasswordHasher().Hash("Zawed-Pass-2026!");
        Assert.Matches(@"^\$argon2id\$v=19\$m=19456,t=2,p=1\$[A-Za-z0-9+/]{22}\$[A-Za-z0-9+/]{43}$", hash);
        Assert.True(new PasswordHasher().Matches("Zawed-Pass-2026!", hash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData("$argon2i$v=19$m=19456,t=2,p=1$AAAA$AAAA")]
    [InlineData("$argon2id$v=19$m=x,t=2,p=1$AAAA$AAAA")]
    public void AMalformedHashNeverMatches(string hash) => Assert.False(new PasswordHasher().Matches("any", hash));

    [Fact]
    public void ValuesEncryptedByTheJavaApiDecryptUnderKeyOneAndUnderAnUnsignedKeyId()
    {
        var ring = new DataEncryptor(200, new Dictionary<int, byte[]> { [1] = Key1, [200] = Key200 });
        Assert.Equal("JBSWY3DPEHPK3PXP", Encoding.UTF8.GetString(ring.Decrypt(Convert.FromBase64String(Golden["aes.key1.value"]), Binding)));
        var under200 = Convert.FromBase64String(Golden["aes.key200.value"]);
        Assert.Equal(200, DataEncryptor.KeyIdOf(under200));
        Assert.Equal("EG-NBE 0001234567", Encoding.UTF8.GetString(ring.Decrypt(under200, Binding)));
    }

    [Fact]
    public void RoundTripsNeverRepeatsACiphertextAndKeepsTheLayout()
    {
        var encryptor = new DataEncryptor(1, new Dictionary<int, byte[]> { [1] = Key1 });
        var a = encryptor.Encrypt("secret"u8.ToArray(), Binding);
        var b = encryptor.Encrypt("secret"u8.ToArray(), Binding);
        Assert.NotEqual(a, b);
        Assert.Equal(1 + 12 + 6 + 16, a.Length);
        Assert.Equal(1, a[0]);
        Assert.Equal("secret", Encoding.UTF8.GetString(encryptor.Decrypt(a, Binding)));
    }

    [Fact]
    public void DetectsAnyChangeAnotherBindingAndAnotherKey()
    {
        var encryptor = new DataEncryptor(1, new Dictionary<int, byte[]> { [1] = Key1 });
        var value = encryptor.Encrypt("secret"u8.ToArray(), Binding);
        for (var i = 1; i < value.Length; i++)
        {
            var changed = value.ToArray();
            changed[i] ^= 1;
            Assert.Throws<ArgumentException>(() => encryptor.Decrypt(changed, Binding));
        }
        Assert.Throws<ArgumentException>(() => encryptor.Decrypt(value, "user:other"u8.ToArray()));
        Assert.Throws<ArgumentException>(() => new DataEncryptor(1, new Dictionary<int, byte[]> { [1] = Key200 }).Decrypt(value, Binding));
    }

    [Fact]
    public void AValueUnderAKeyThisInstanceDoesNotHoldFailsLoudlyAndTheKeyIdIsAuthenticated()
    {
        var two = new DataEncryptor(2, new Dictionary<int, byte[]> { [2] = Key200 });
        var value = two.Encrypt("x"u8.ToArray(), Binding);
        var onlyOne = new DataEncryptor(1, new Dictionary<int, byte[]> { [1] = Key1 });
        Assert.Contains("Unknown key id 2", Assert.Throws<ArgumentException>(() => onlyOne.Decrypt(value, Binding)).Message);

        var relabelled = value.ToArray();
        relabelled[0] = 1;
        var both = new DataEncryptor(2, new Dictionary<int, byte[]> { [1] = Key200, [2] = Key1 });
        Assert.Throws<ArgumentException>(() => both.Decrypt(relabelled, Binding));
    }

    [Fact]
    public void ReencryptionMovesAValueToTheCurrentKeyWithTheSameBindingAndLength()
    {
        var old = new DataEncryptor(1, new Dictionary<int, byte[]> { [1] = Key1 }).Encrypt("1234567890"u8.ToArray(), Binding);
        var rotated = new DataEncryptor(200, new Dictionary<int, byte[]> { [1] = Key1, [200] = Key200 });
        var moved = rotated.Reencrypt(old, Binding);
        Assert.Equal(200, DataEncryptor.KeyIdOf(moved));
        Assert.Equal(old.Length, moved.Length);
        Assert.Equal("1234567890", Encoding.UTF8.GetString(rotated.Decrypt(moved, Binding)));
    }

    [Fact]
    public void RefusesAKeyRingItCannotUse()
    {
        Assert.Throws<ArgumentException>(() => new DataEncryptor(2, new Dictionary<int, byte[]> { [1] = Key1 }));
        Assert.Throws<ArgumentException>(() => new DataEncryptor(1, new Dictionary<int, byte[]> { [1] = new byte[16] }));
        Assert.Throws<ArgumentException>(() => new DataEncryptor(0, new Dictionary<int, byte[]> { [0] = Key1 }));
    }

    [Fact]
    public void LookupHashesAndTheKeyFingerprintEqualTheJavaOnes()
    {
        var lookup = new LookupHasher(Key1);
        Assert.Equal(Golden["lookup.tag"], lookup.Hash("tag-uid", "04A1B2C3D4E5F6"));
        Assert.Equal(Golden["lookup.fingerprint"], DataKeyState.Fingerprint(lookup));
        Assert.NotEqual(lookup.Hash("a", "v"), lookup.Hash("b", "v"));
    }

    [Fact]
    public void SecureTokensHaveTheirShapes()
    {
        Assert.Matches("^[A-Za-z0-9_-]{43}$", SecureTokens.Random(32));
        Assert.Matches("^[ABCDEFGHJKMNPQRSTUVWXYZ23456789]{8}$", SecureTokens.Code(8));
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", SecureTokens.Sha256Hex("abc"));
        Assert.True(SecureTokens.ConstantTimeEquals("a", "a"));
        Assert.False(SecureTokens.ConstantTimeEquals("a", null));
    }
}
