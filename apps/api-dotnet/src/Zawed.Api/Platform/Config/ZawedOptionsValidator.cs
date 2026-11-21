using Microsoft.Extensions.Options;

namespace Zawed.Api.Platform.Config;

/// <summary>
/// Every boot-time check of the Java <c>ZawedProperties</c> (its <c>@AssertTrue</c> methods), with the
/// same messages. Runs at startup (<c>ValidateOnStart</c>), so a misconfigured instance never serves.
/// </summary>
public sealed class ZawedOptionsValidator : IValidateOptions<ZawedOptions>
{
    public const int MinJwtSecretBytes = 32;
    public const int DataKeyBytes = 32;
    public const int DefaultDataKeyId = 1;
    public const int MaxDataKeyId = 255;
    public const int MinBootstrapTokenChars = 32;

    /// <summary>The development values committed in appsettings; a deployed instance refuses every one.</summary>
    public const string CommittedLocalDataKey = "bG9jYWwtb25seS1kYXRhLWtleS1ub3QtZm9yLXByb2Q=";

    public static readonly IReadOnlySet<string> CommittedLocalValues = new HashSet<string>
    {
        "local-only-jwt-secret-do-not-deploy-0123456789abcdef",
        CommittedLocalDataKey,
        "local-only-bootstrap-token-do-not-deploy",
    };

    public ValidateOptionsResult Validate(string? name, ZawedOptions options)
    {
        var failures = Failures(options);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>The messages of every rule the options break; empty when they are safe.</summary>
    public static List<string> Failures(ZawedOptions o)
    {
        var failures = new List<string>();
        var s = o.Security;

        if (o.Environment is null) failures.Add("zawed.environment (ZAWED_ENVIRONMENT) is required");
        if (o.Cors.Origins().Count == 0)
            failures.Add("zawed.cors.allowed-origins (ZAWED_CORS_ALLOWED_ORIGINS) is required");
        if (string.IsNullOrWhiteSpace(s.JwtSecret)) failures.Add("zawed.security.jwt-secret (ZAWED_JWT_SECRET) is required");
        if (string.IsNullOrWhiteSpace(s.DataKey)) failures.Add("zawed.security.data-key (ZAWED_DATA_KEY) is required");

        if (!IsCorsSafe(o))
            failures.Add("zawed.cors.allowed-origins must list explicit origins; a deployed environment accepts only "
                + "https origins and never localhost");
        if (!string.IsNullOrWhiteSpace(s.JwtSecret) && s.JwtSecretBytes().Length < MinJwtSecretBytes)
            failures.Add("zawed.security.jwt-secret must be at least 32 bytes");
        if (!IsDataKeyValid(s))
            failures.Add("zawed.security.data-key must be the base64 of exactly 32 random bytes");
        if (s.BootstrapEnabled() && s.BootstrapToken!.Length < MinBootstrapTokenChars)
            failures.Add("zawed.security.bootstrap-token must be at least 32 characters when set");
        if (s.DataKeyId is { } id && (id < 1 || id > MaxDataKeyId))
            failures.Add("zawed.security.data-key-id (ZAWED_DATA_KEY_ID) must be a key id from 1 to 255");

        List<KeyValuePair<int, byte[]>> previous;
        try
        {
            previous = s.PreviousDataKeys();
        }
        catch (FormatException)
        {
            failures.Add("zawed.security.data-keys-previous (ZAWED_DATA_KEYS_PREVIOUS) must be comma-separated "
                + "id:base64 entries, each id from 1 to 255 and each key the base64 of exactly 32 bytes");
            previous = [];
        }
        if (previous.Select(p => p.Key).Distinct().Count() != previous.Count)
            failures.Add("zawed.security.data-keys-previous (ZAWED_DATA_KEYS_PREVIOUS) lists the same key id twice");
        if (previous.Any(p => p.Key == s.CurrentDataKeyId()))
            failures.Add("zawed.security.data-key-id (ZAWED_DATA_KEY_ID) is also listed in data-keys-previous "
                + "(ZAWED_DATA_KEYS_PREVIOUS): the current key is never a previous one");
        if (IsDataKeyValid(s) && s.DataKey is not null)
        {
            var all = new List<byte[]> { s.DataKeyBytes() };
            all.AddRange(previous.Select(p => p.Value));
            if (all.Select(Convert.ToBase64String).Distinct().Count() != all.Count)
                failures.Add("the same data key appears under two ids (ZAWED_DATA_KEY and ZAWED_DATA_KEYS_PREVIOUS): "
                    + "a rotation needs a newly generated key");
        }

        var lookupValid = IsLookupKeyValid(s);
        if (!lookupValid)
            failures.Add("zawed.security.lookup-key (ZAWED_LOOKUP_KEY) must be the base64 of exactly 32 bytes when set");

        // Once the current data key is not key 1, the original key must be named explicitly (ADR 0027).
        if (s.CurrentDataKeyId() != DefaultDataKeyId)
        {
            var pinned = s.HasLookupKey()
                && (!lookupValid || s.DataKey is null || !IsDataKeyValid(s) || !s.LookupKeyBytes().AsSpan().SequenceEqual(s.DataKeyBytes()));
            if (!pinned)
                failures.Add("zawed.security.lookup-key (ZAWED_LOOKUP_KEY) must be set once ZAWED_DATA_KEY_ID is not 1, "
                    + "to the key the lookup hashes were made with (the original ZAWED_DATA_KEY), never to the current data key");
        }

        // While key 1 is in the ring, the lookup key must be exactly key 1.
        if (s.HasLookupKey() && lookupValid && s.DataKey is not null && IsDataKeyValid(s))
        {
            var keyOne = s.CurrentDataKeyId() == DefaultDataKeyId
                ? s.DataKeyBytes()
                : previous.Where(p => p.Key == DefaultDataKeyId).Select(p => p.Value).FirstOrDefault();
            if (keyOne is not null && !s.LookupKeyBytes().AsSpan().SequenceEqual(keyOne))
                failures.Add("zawed.security.lookup-key (ZAWED_LOOKUP_KEY) must be the original data key: while key 1 is "
                    + "in the ring (ZAWED_DATA_KEY with id 1, or 1: in ZAWED_DATA_KEYS_PREVIOUS) it must be exactly key 1");
        }

        if (o.Environment is { } env && env.IsDeployed())
        {
            var committedKey = Convert.FromBase64String(CommittedLocalDataKey);
            var own = s.CookieSecure
                && !CommittedLocalValues.Contains(s.JwtSecret ?? "")
                && !CommittedLocalValues.Contains(s.DataKey ?? "")
                && (!s.BootstrapEnabled() || !CommittedLocalValues.Contains(s.BootstrapToken!))
                && previous.All(p => !p.Value.AsSpan().SequenceEqual(committedKey))
                && (!s.HasLookupKey() || !lookupValid || !s.LookupKeyBytes().AsSpan().SequenceEqual(committedKey));
            if (!own)
                failures.Add("a deployed environment must set its own ZAWED_JWT_SECRET, ZAWED_DATA_KEY and "
                    + "ZAWED_BOOTSTRAP_TOKEN (the committed development values are refused, also as a previous data key or "
                    + "as ZAWED_LOOKUP_KEY), and send cookies Secure");
        }
        return failures;
    }

    private static bool IsCorsSafe(ZawedOptions o)
    {
        if (o.Environment is null) return true;
        foreach (var origin in o.Cors.Origins())
        {
            if (origin is null || origin.Contains('*')) return false;
            if (o.Environment.Value.IsDeployed() && (!origin.StartsWith("https://", StringComparison.Ordinal) || IsLoopback(origin)))
                return false;
        }
        return true;
    }

    private static bool IsDataKeyValid(ZawedOptions.SecurityOptions s)
    {
        if (s.DataKey is null) return true;
        try
        {
            return s.DataKeyBytes().Length == DataKeyBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsLookupKeyValid(ZawedOptions.SecurityOptions s)
    {
        if (!s.HasLookupKey()) return true;
        try
        {
            return s.LookupKeyBytes().Length == DataKeyBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsLoopback(string origin) =>
        origin.Contains("://localhost", StringComparison.Ordinal) || origin.Contains("://127.", StringComparison.Ordinal)
        || origin.Contains("://[::1]", StringComparison.Ordinal);
}
