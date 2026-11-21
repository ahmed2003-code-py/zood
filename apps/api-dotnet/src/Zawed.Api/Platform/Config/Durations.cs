using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Zawed.Api.Platform.Config;

/// <summary>
/// Durations as the Java API reads and writes them (Spring's <c>DurationStyle</c>, <c>java.time.Duration</c>), so a
/// setting such as <c>PT6H</c>, <c>P90D</c> or <c>10m</c> means the same to both APIs and <c>maxAge</c> in
/// <c>GET /ops/jobs</c> is the same text (<c>PT27H</c>).
/// </summary>
public static partial class Durations
{
    [GeneratedRegex("^([+-]?[0-9]+)([a-zA-Z]{0,2})$", RegexOptions.CultureInvariant)]
    private static partial Regex Simple();

    /// <summary>
    /// ISO-8601 (<c>PT6H</c>, <c>P90D</c>, <c>P1DT2H</c>) or Spring's simple style (<c>500ms</c>, <c>10s</c>,
    /// <c>10m</c>, <c>6h</c>, <c>30d</c>; a bare number is milliseconds).
    /// </summary>
    public static TimeSpan Parse(string text)
    {
        var value = text.Trim();
        var simple = Simple().Match(value);
        if (simple.Success)
        {
            var amount = long.Parse(simple.Groups[1].Value, CultureInfo.InvariantCulture);
            return simple.Groups[2].Value.ToLowerInvariant() switch
            {
                "" or "ms" => TimeSpan.FromMilliseconds(amount),
                "ns" => TimeSpan.FromTicks(amount / 100),
                "us" => TimeSpan.FromMicroseconds(amount),
                "s" => TimeSpan.FromSeconds(amount),
                "m" => TimeSpan.FromMinutes(amount),
                "h" => TimeSpan.FromHours(amount),
                "d" => TimeSpan.FromDays(amount),
                _ => throw new FormatException($"'{text}' is not a duration"),
            };
        }
        try
        {
            return System.Xml.XmlConvert.ToTimeSpan(value.ToUpperInvariant());
        }
        catch (FormatException ex)
        {
            throw new FormatException($"'{text}' is not a duration (ISO-8601 such as PT6H, or 6h)", ex);
        }
    }

    /// <summary>A duration setting, or its default when it is not set.</summary>
    public static TimeSpan Get(IConfiguration configuration, string key, TimeSpan defaultValue) =>
        configuration[key] is { Length: > 0 } text ? Parse(text) : defaultValue;

    /// <summary>
    /// <c>java.time.Duration.toString()</c>: <c>PT27H</c>, <c>PT1H30M</c>, <c>PT720H</c> for 30 days, <c>PT1.5S</c>,
    /// <c>PT0S</c>. Days are written as hours, as Java does.
    /// </summary>
    public static string ToJava(TimeSpan duration)
    {
        if (duration == TimeSpan.Zero)
        {
            return "PT0S";
        }
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "A job's window is positive");
        }
        var totalSeconds = duration.Ticks / TimeSpan.TicksPerSecond;
        var fractionTicks = duration.Ticks % TimeSpan.TicksPerSecond;
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;
        var text = new StringBuilder("PT");
        if (hours != 0)
        {
            text.Append(hours.ToString(CultureInfo.InvariantCulture)).Append('H');
        }
        if (minutes != 0)
        {
            text.Append(minutes.ToString(CultureInfo.InvariantCulture)).Append('M');
        }
        if (seconds == 0 && fractionTicks == 0)
        {
            return text.ToString();
        }
        text.Append(seconds.ToString(CultureInfo.InvariantCulture));
        if (fractionTicks != 0)
        {
            text.Append('.').Append((fractionTicks * 100).ToString("D9", CultureInfo.InvariantCulture).TrimEnd('0'));
        }
        return text.Append('S').ToString();
    }
}
