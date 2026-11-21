using System.Globalization;
using System.Text.RegularExpressions;

namespace Zawed.Api.Modules.Network;

/// <summary>
/// A station's time zone as Java's <c>ZoneId.of(zone).getId()</c> reads and normalises it, so a value stored by either API
/// means the same to both:
/// <list type="bullet">
/// <item>a fixed offset (<c>Z</c>, <c>+2</c>, <c>+02</c>, <c>+0200</c>, <c>+02:00</c>, <c>+02:00:30</c>, up to ±18:00) is
/// <c>Z</c> or <c>+hh:mm[:ss]</c>;</item>
/// <item><c>UTC</c>, <c>GMT</c> and <c>UT</c>, alone or followed by an offset, are kept with the offset normalised
/// (<c>UTC+2</c> is <c>UTC+02:00</c>, <c>GMT+0</c> is <c>GMT</c>);</item>
/// <item>anything else must be a region id of the time-zone database, spelled exactly (case matters).</item>
/// </list>
/// </summary>
public static partial class Zones
{
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9~/._+-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RegionId();

    /// <summary>The normalised id, or null for a zone Java would refuse.</summary>
    public static string? Normalise(string zone)
    {
        if (zone.Length <= 1 || zone[0] is '+' or '-')
        {
            return Offset(zone) is { } offset ? OffsetId(offset) : null;
        }
        foreach (var prefix in new[] { "UTC", "GMT", "UT" })
        {
            if (zone == prefix)
            {
                return prefix;
            }
            if (zone.StartsWith(prefix, StringComparison.Ordinal) && zone[prefix.Length] is '+' or '-')
            {
                return Offset(zone[prefix.Length..]) is { } offset ? offset == TimeSpan.Zero ? prefix : prefix + OffsetId(offset) : null;
            }
        }
        if (!RegionId().IsMatch(zone) || zone.StartsWith("posix/", StringComparison.Ordinal) || zone.StartsWith("right/", StringComparison.Ordinal))
        {
            return null;
        }
        try
        {
            var info = TimeZoneInfo.FindSystemTimeZoneById(zone);
            return info.HasIanaId && string.Equals(info.Id, zone, StringComparison.Ordinal) ? zone : null;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>The rules of a stored (normalised) zone, for evaluating limits and receipts in the station's time.</summary>
    public static TimeZoneInfo Info(string zone)
    {
        if (zone is "UTC" or "GMT" or "UT" or "Z")
        {
            return TimeZoneInfo.Utc;
        }
        var offsetText = zone[0] is '+' or '-' ? zone : zone.StartsWith("UT", StringComparison.Ordinal) || zone.StartsWith("GMT", StringComparison.Ordinal)
            ? zone.TrimStart('U', 'T', 'C', 'G', 'M') : null;
        if (offsetText is not null && Offset(offsetText) is { } offset)
        {
            return TimeZoneInfo.CreateCustomTimeZone(zone, offset, zone, zone);
        }
        return TimeZoneInfo.FindSystemTimeZoneById(zone);
    }

    /// <summary>Java <c>ZoneOffset.of</c>: <c>Z</c>, <c>±h</c>, <c>±hh</c>, <c>±hh:mm</c>, <c>±hhmm</c>, <c>±hh:mm:ss</c>, <c>±hhmmss</c>.</summary>
    private static TimeSpan? Offset(string text)
    {
        if (text == "Z")
        {
            return TimeSpan.Zero;
        }
        if (text.Length < 2 || text[0] is not ('+' or '-'))
        {
            return null;
        }
        int hours, minutes = 0, seconds = 0;
        var body = text[1..];
        bool Two(string s, int at, out int value) =>
            int.TryParse(s.AsSpan(at, 2), NumberStyles.None, CultureInfo.InvariantCulture, out value);
        switch (body.Length)
        {
            case 1:
                if (!char.IsAsciiDigit(body[0])) return null;
                hours = body[0] - '0';
                break;
            case 2:
                if (!Two(body, 0, out hours)) return null;
                break;
            case 4:
                if (!Two(body, 0, out hours) || !Two(body, 2, out minutes)) return null;
                break;
            case 5:
                if (body[2] != ':' || !Two(body, 0, out hours) || !Two(body, 3, out minutes)) return null;
                break;
            case 6:
                if (!Two(body, 0, out hours) || !Two(body, 2, out minutes) || !Two(body, 4, out seconds)) return null;
                break;
            case 8:
                if (body[2] != ':' || body[5] != ':' || !Two(body, 0, out hours) || !Two(body, 3, out minutes) || !Two(body, 6, out seconds))
                {
                    return null;
                }
                break;
            default:
                return null;
        }
        if (hours > 18 || minutes > 59 || seconds > 59 || hours == 18 && (minutes > 0 || seconds > 0))
        {
            return null;
        }
        var total = new TimeSpan(hours, minutes, seconds);
        return text[0] == '-' ? -total : total;
    }

    private static string OffsetId(TimeSpan offset)
    {
        if (offset == TimeSpan.Zero)
        {
            return "Z";
        }
        var abs = offset.Duration();
        var id = (offset < TimeSpan.Zero ? "-" : "+") + abs.Hours.ToString("D2", CultureInfo.InvariantCulture) + ":"
            + abs.Minutes.ToString("D2", CultureInfo.InvariantCulture);
        return abs.Seconds == 0 ? id : id + ":" + abs.Seconds.ToString("D2", CultureInfo.InvariantCulture);
    }
}
