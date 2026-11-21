using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Zawed.Api.Platform.Config;

namespace Zawed.Api.Platform.Crypto;

/// <summary>
/// A keyed, deterministic hash for finding a secret value without storing it (tag UIDs, activation codes,
/// receipt links), identical to the Java <c>LookupHasher</c>: each namespace derives its own key,
/// <c>HMAC(master, "zawed-lookup:" + namespace)</c>, and the value's hash is the lowercase hex
/// <c>HMAC(namespaceKey, value)</c>. The master key is the lookup key, which does not rotate (ADR 0027).
/// </summary>
public sealed class LookupHasher
{
    private readonly byte[] masterKey;

    public LookupHasher(IOptions<ZawedOptions> options) : this(options.Value.Security.LookupKeyBytes())
    {
    }

    public LookupHasher(byte[] masterKey) => this.masterKey = masterKey.ToArray();

    public string Hash(string @namespace, string value)
    {
        var key = HMACSHA256.HashData(masterKey, Encoding.UTF8.GetBytes("zawed-lookup:" + @namespace));
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value)));
    }
}
