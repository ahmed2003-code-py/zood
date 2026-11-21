using System.Globalization;
using System.Text.RegularExpressions;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Notifications;

/// <summary>
/// The feed's cursor (Java <c>Notifications.Keyset</c>, B7 W5 review L4): the last row's <c>created_at</c> in epoch
/// microseconds (the database's precision) and its id, as <c>&lt;micros&gt;.&lt;uuid&gt;</c>. Opaque to the client;
/// anything else is <c>400 INVALID_CURSOR</c>, never a 500.
/// </summary>
public sealed partial record Keyset(DateTimeOffset CreatedAt, Guid Id)
{
    /// <summary>9999-12-31T23:59:59Z in epoch microseconds.</summary>
    private const long LatestMicros = 253_402_300_799_000_000L;

    public static string Of(Notification last)
    {
        var micros = (last.CreatedAt.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10; // Java ChronoUnit.MICROS.between
        return micros.ToString(CultureInfo.InvariantCulture) + "." + last.Id;
    }

    public static Keyset? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var match = Format().Match(value);
        // A cursor before 1970 or after 9999 can only be forged: PostgreSQL would refuse it with "timestamp out of
        // range", which is a 500, not a bad cursor. Long.parseLong refuses what does not fit in 64 bits.
        if (match.Success
            && long.TryParse(match.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var micros)
            && micros is >= 0 and <= LatestMicros
            && JavaUuid(match.Groups[2].Value) is { } id)
        {
            return new Keyset(DateTimeOffset.UnixEpoch.AddTicks(micros * 10), id);
        }
        throw new ApiException(400, "INVALID_CURSOR", "Invalid cursor", "cursor");
    }

    /// <summary>
    /// Java's <c>UUID.fromString</c> on 36 characters of hex digits and dashes: the canonical 8-4-4-4-12 form, or else
    /// its lenient <c>fromString1</c> (exactly five dash-separated groups of hex, each masked to its field's width). Null
    /// where Java throws.
    /// </summary>
    private static Guid? JavaUuid(string name)
    {
        if (Guid.TryParseExact(name, "D", out var canonical))
        {
            return canonical;
        }
        var dash1 = name.IndexOf('-', 0);
        var dash2 = name.IndexOf('-', dash1 + 1);
        var dash3 = name.IndexOf('-', dash2 + 1);
        var dash4 = name.IndexOf('-', dash3 + 1);
        var dash5 = name.IndexOf('-', dash4 + 1);
        if (dash1 < 0 || dash2 < 0 || dash3 < 0 || dash4 < 0 || dash5 >= 0)
        {
            return null;
        }
        if (Hex(name, 0, dash1) is not { } p1 || Hex(name, dash1 + 1, dash2) is not { } p2 || Hex(name, dash2 + 1, dash3) is not { } p3
            || Hex(name, dash3 + 1, dash4) is not { } p4 || Hex(name, dash4 + 1, name.Length) is not { } p5)
        {
            return null;
        }
        var most = ((((p1 & 0xffffffffL) << 16) | (p2 & 0xffffL)) << 16) | (p3 & 0xffffL);
        var least = ((p4 & 0xffffL) << 48) | (p5 & 0xffffffffffffL);
        return Guid.ParseExact(most.ToString("x16", CultureInfo.InvariantCulture) + least.ToString("x16", CultureInfo.InvariantCulture), "N");
    }

    /// <summary>
    /// Java <c>Long.parseLong(s, begin, end, 16)</c> on hex digits only: null when empty or above <c>Long.MAX_VALUE</c>.
    /// .NET reads 16 hex digits as two's complement, so a negative result is Java's overflow.
    /// </summary>
    private static long? Hex(string s, int begin, int end) =>
        end > begin && long.TryParse(s.AsSpan(begin, end - begin), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v)
            && v >= 0
            ? v
            : null;

    [GeneratedRegex(@"^(-?[0-9]{1,19})\.([0-9a-fA-F-]{36})\z", RegexOptions.CultureInvariant)]
    private static partial Regex Format();
}
