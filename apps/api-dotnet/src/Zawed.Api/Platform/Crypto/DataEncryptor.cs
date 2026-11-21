using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Zawed.Api.Platform.Config;

namespace Zawed.Api.Platform.Crypto;

/// <summary>
/// Encrypts small secrets at rest (TOTP secrets, tag UIDs, bank account numbers) with AES-256-GCM, in the
/// exact format of the Java <c>DataEncryptor</c>, so values written by either API read in the other:
/// <c>[key id: 1 byte][nonce: 12][ciphertext][tag: 16]</c>, with associated data = the key id byte followed
/// by the caller's row binding (fed as one buffer, which GCM authenticates identically to Java's two
/// <c>updateAAD</c> calls). Values are written with the current key and read with the key their first
/// byte names (unsigned); an unknown id fails loudly and no other key is tried (ADR 0027).
/// <c>DataEncryptorTests</c> decrypts values written by the Java code.
/// </summary>
public sealed class DataEncryptor
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int ShortestValue = 1 + NonceBytes + TagBytes;

    private readonly Dictionary<int, byte[]> keys;

    public DataEncryptor(IOptions<ZawedOptions> options)
        : this(options.Value.Security.CurrentDataKeyId(), options.Value.Security.DataKeys())
    {
    }

    public DataEncryptor(int currentKeyId, IReadOnlyDictionary<int, byte[]> keyBytesById)
    {
        keys = [];
        foreach (var (id, bytes) in keyBytesById)
        {
            if (id is < 1 or > 255)
            {
                throw new ArgumentException("A data key id is 1 to 255");
            }
            if (bytes.Length != 32)
            {
                throw new ArgumentException("AES-256 needs a 32-byte key");
            }
            keys[id] = bytes.ToArray();
        }
        if (!keys.ContainsKey(currentKeyId))
        {
            throw new ArgumentException("The current data key is not in the key ring");
        }
        CurrentKeyId = currentKeyId;
    }

    /// <summary>The id new values are written with.</summary>
    public int CurrentKeyId { get; }

    public bool KnowsKey(int keyId) => keys.ContainsKey(keyId);

    /// <summary>The key id a value was written with (its first byte, 1 to 255).</summary>
    public static int KeyIdOf(byte[] value)
    {
        if (value is null || value.Length < ShortestValue)
        {
            throw new ArgumentException("Not an encrypted value");
        }
        return value[0];
    }

    public byte[] Encrypt(byte[] plaintext, byte[] associatedData)
    {
        var output = new byte[1 + NonceBytes + plaintext.Length + TagBytes];
        output[0] = (byte)CurrentKeyId;
        var nonce = output.AsSpan(1, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(keys[CurrentKeyId], TagBytes);
        aes.Encrypt(nonce, plaintext, output.AsSpan(1 + NonceBytes, plaintext.Length),
            output.AsSpan(1 + NonceBytes + plaintext.Length, TagBytes), Aad(output[0], associatedData));
        return output;
    }

    /// <summary>
    /// Fails when the value was changed, is bound to other associated data, or names a key this instance
    /// does not hold.
    /// </summary>
    public byte[] Decrypt(byte[] value, byte[] associatedData)
    {
        var keyId = KeyIdOf(value);
        if (!keys.TryGetValue(keyId, out var key))
        {
            throw new ArgumentException($"Unknown key id {keyId}: this instance does not hold that data key");
        }
        var length = value.Length - ShortestValue;
        var plaintext = new byte[length];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(value.AsSpan(1, NonceBytes), value.AsSpan(1 + NonceBytes, length), value.AsSpan(1 + NonceBytes + length, TagBytes),
                plaintext, Aad(value[0], associatedData));
            return plaintext;
        }
        catch (CryptographicException ex)
        {
            throw new ArgumentException("The encrypted value could not be verified", ex);
        }
    }

    /// <summary>The same plaintext under the current key, same binding, same length (V20's guards check it).</summary>
    public byte[] Reencrypt(byte[] value, byte[] associatedData)
    {
        var plaintext = Decrypt(value, associatedData);
        try
        {
            return Encrypt(plaintext, associatedData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] Aad(byte keyId, byte[] binding)
    {
        var aad = new byte[1 + binding.Length];
        aad[0] = keyId;
        binding.CopyTo(aad, 1);
        return aad;
    }
}
