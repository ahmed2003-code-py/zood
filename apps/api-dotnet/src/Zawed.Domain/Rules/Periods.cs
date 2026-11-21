namespace Zawed.Domain.Rules;

/// <summary>
/// Limit periods, evaluated in the station's time zone (PRD 2.3: store UTC, evaluate business rules in the station's
/// configured zone; Java <c>rules.Periods</c>). A week starts on Saturday, the Egyptian work week. Ported from the
/// demo's <c>calendar.ts</c>, whose behaviour the golden tests pin.
/// </summary>
public static class Periods
{
    /// <summary>A limit's period.</summary>
    public enum Period
    {
        DAY,
        WEEK,
        MONTH,
    }

    /// <summary>The local date the period containing <paramref name="at"/> starts on.</summary>
    public static DateOnly StartDate(DateTimeOffset at, Period period, TimeZoneInfo zone)
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
        return period switch
        {
            Period.DAY => day,
            // previousOrSame(SATURDAY)
            Period.WEEK => day.AddDays(-(((int)day.DayOfWeek - (int)DayOfWeek.Saturday + 7) % 7)),
            Period.MONTH => new DateOnly(day.Year, day.Month, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(period)),
        };
    }

    /// <summary>
    /// The instant the period containing <paramref name="at"/> starts: local midnight of its first day. Uses the
    /// zone's own rules, so a day after a daylight-saving change still starts at local midnight.
    /// </summary>
    public static DateTimeOffset Start(DateTimeOffset at, Period period, TimeZoneInfo zone) =>
        AtStartOfDay(StartDate(at, period, zone), zone);

    /// <summary>
    /// The instant the period containing <paramref name="at"/> ends, which is where the next one starts. Usage in a
    /// period is counted in <c>[start, end)</c>: an offline capture (B5) is counted in the period of its own time,
    /// which may already be over when it reaches the server.
    /// </summary>
    public static DateTimeOffset End(DateTimeOffset at, Period period, TimeZoneInfo zone)
    {
        var first = StartDate(at, period, zone);
        var next = period switch
        {
            Period.DAY => first.AddDays(1),
            Period.WEEK => first.AddDays(7),
            Period.MONTH => first.AddMonths(1),
            _ => throw new ArgumentOutOfRangeException(nameof(period)),
        };
        return AtStartOfDay(next, zone);
    }

    /// <summary>Two instants fall in the same period exactly when their periods start on the same day.</summary>
    public static bool SamePeriod(DateTimeOffset a, DateTimeOffset b, Period period, TimeZoneInfo zone) =>
        StartDate(a, period, zone) == StartDate(b, period, zone);

    /// <summary>
    /// Java's <c>LocalDate.atStartOfDay(zone)</c>, in UTC: midnight in the zone. When midnight falls in a gap (Egypt
    /// moves its clocks forward at 00:00), the first instant after the gap; when it falls in an overlap, the earlier
    /// offset. Both are midnight read with the offset in force before the transition.
    /// </summary>
    public static DateTimeOffset AtStartOfDay(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var offset = zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local)
            ? zone.GetUtcOffset(local.AddDays(-1))
            : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
