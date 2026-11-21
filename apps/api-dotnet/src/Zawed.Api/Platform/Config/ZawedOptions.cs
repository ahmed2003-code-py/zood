using System.Text;

namespace Zawed.Api.Platform.Config;

/// <summary>Where this instance runs. Port of <c>ZawedProperties.Environment</c>.</summary>
public enum ZawedEnvironment
{
    LOCAL,
    TEST,
    DEV,
    STAGING,
    PRODUCTION,
}

public static class ZawedEnvironmentExtensions
{
    /// <summary>True for anything reachable from the internet.</summary>
    public static bool IsDeployed(this ZawedEnvironment environment) =>
        environment is ZawedEnvironment.DEV or ZawedEnvironment.STAGING or ZawedEnvironment.PRODUCTION;
}

/// <summary>
/// ZAWED's own configuration (section <c>Zawed</c>), checked once at boot by
/// <see cref="ZawedOptionsValidator"/>. A deploy with unsafe values refuses to start instead of running
/// quietly misconfigured. Same names and rules as the Java <c>ZawedProperties</c>; the environment
/// variables are the same (<see cref="ZawedConfiguration"/> maps them).
/// </summary>
public sealed class ZawedOptions
{
    public const string Section = "Zawed";

    /// <summary>Null when the variable is missing; the validator refuses it.</summary>
    public ZawedEnvironment? Environment { get; set; }

    public CorsOptions Cors { get; set; } = new();

    public SecurityOptions Security { get; set; } = new();

    /// <summary>Whether the interactive API reference is served at /api/v1/docs (ZAWED_API_DOCS).</summary>
    public bool ApiDocs { get; set; } = true;

    public sealed class CorsOptions
    {
        /// <summary>
        /// Explicit origins, comma-separated, as ZAWED_CORS_ALLOWED_ORIGINS gives them. One string rather than
        /// a list, so a value set later replaces the defaults whole instead of merging by index.
        /// </summary>
        public string? AllowedOrigins { get; set; }

        /// <summary>The origins, trimmed, empty entries dropped.</summary>
        public IReadOnlyList<string> Origins() =>
            (AllowedOrigins ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// The secrets. <see cref="ToString"/> never prints a value.
    /// </summary>
    public sealed class SecurityOptions
    {
        /// <summary>HS256 key of access tokens, at least 32 bytes.</summary>
        public string? JwtSecret { get; set; }

        /// <summary>Base64 of the current 32-byte AES key.</summary>
        public string? DataKey { get; set; }

        /// <summary>The current key's id, 1 to 255; 1 when not set (ADR 0027).</summary>
        public int? DataKeyId { get; set; }

        /// <summary>Decrypt-only keys during a rotation: <c>id:base64</c>, comma-separated.</summary>
        public string? DataKeysPrevious { get; set; }

        /// <summary>Base64 key of the lookup hashes; when unset, the data key (allowed only while its id is 1).</summary>
        public string? LookupKey { get; set; }

        /// <summary>Optional; enables the one-time creation of the first Super Admin.</summary>
        public string? BootstrapToken { get; set; }

        /// <summary>Whether session cookies carry Secure; always true when deployed.</summary>
        public bool CookieSecure { get; set; }

        /// <summary>The second factor at sign-in (ZAWED_MFA_REQUIRED). Production refuses false.</summary>
        public bool MfaRequired { get; set; } = true;

        public byte[] JwtSecretBytes() => Encoding.UTF8.GetBytes(JwtSecret ?? "");

        public byte[] DataKeyBytes() => Convert.FromBase64String(DataKey ?? "");

        public int CurrentDataKeyId() => DataKeyId ?? ZawedOptionsValidator.DefaultDataKeyId;

        /// <summary>
        /// The previous keys in the order listed, duplicates included (the validator refuses them).
        /// </summary>
        /// <exception cref="FormatException">An entry is not <c>id:base64</c> of 32 bytes with an id from 1 to 255.</exception>
        public List<KeyValuePair<int, byte[]>> PreviousDataKeys()
        {
            var keys = new List<KeyValuePair<int, byte[]>>();
            if (string.IsNullOrWhiteSpace(DataKeysPrevious))
            {
                return keys;
            }
            foreach (var entry in DataKeysPrevious.Split(','))
            {
                var trimmed = entry.Trim();
                var colon = trimmed.IndexOf(':');
                if (colon <= 0)
                {
                    throw new FormatException("A previous data key is not written as id:base64");
                }
                var id = int.Parse(trimmed[..colon].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                var key = Convert.FromBase64String(trimmed[(colon + 1)..].Trim());
                if (id < 1 || id > ZawedOptionsValidator.MaxDataKeyId || key.Length != ZawedOptionsValidator.DataKeyBytes)
                {
                    throw new FormatException("A previous data key has a bad id or length");
                }
                keys.Add(new(id, key));
            }
            return keys;
        }

        /// <summary>Every key this instance can decrypt with, by id: the current one, then the previous ones.</summary>
        public Dictionary<int, byte[]> DataKeys()
        {
            var keys = new Dictionary<int, byte[]> { [CurrentDataKeyId()] = DataKeyBytes() };
            foreach (var previous in PreviousDataKeys())
            {
                keys.TryAdd(previous.Key, previous.Value);
            }
            return keys;
        }

        public bool HasLookupKey() => !string.IsNullOrWhiteSpace(LookupKey);

        /// <summary>The key of the lookup hashes: its own value when set, otherwise the data key (key 1).</summary>
        public byte[] LookupKeyBytes() => HasLookupKey() ? Convert.FromBase64String(LookupKey!.Trim()) : DataKeyBytes();

        public bool BootstrapEnabled() => !string.IsNullOrWhiteSpace(BootstrapToken);

        public override string ToString() =>
            $"Security[secrets hidden, dataKeyId={CurrentDataKeyId()}, cookieSecure={CookieSecure}]";
    }
}
