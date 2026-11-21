using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Web;
using Zawed.Domain.Rules;

namespace Zawed.Api.Modules.Fueling;

/// <summary>One Cairo day of the activity calendar (Java <c>Activity.ActivityDay</c>).</summary>
public sealed record ActivityDay(DateOnly Day, int Completed, int Declined, int Offline, int WithConflicts, long VolumeMl, long AmountPiastres,
    int Alerts);

/// <summary>What the calendar is about. Null fields do not filter.</summary>
public sealed record ActivitySubject(Guid? CompanyId, Guid? OperatorId, Guid? StationId, Guid? VehicleId, Guid? DeviceId, Guid? AgentId);

/// <summary>
/// The activity calendar (plan §7 B6; Java <c>Activity</c>): for each Cairo day of a range, what happened to the entity in
/// view: fuelings completed and declined, litres, amount, alerts. Read in the caller's scope, so a company's calendar counts
/// only its own fuelings and an operator's only those at its stations.
/// </summary>
public sealed class Activity(IDbSession session)
{
    public const int MaxDays = 366;
    public static readonly TimeZoneInfo Cairo = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");

    private sealed class AlertRow
    {
        public DateOnly Day { get; set; }
        public long Alerts { get; set; }
    }

    private sealed class DayRow
    {
        public DateOnly Day { get; set; }
        public long Completed { get; set; }
        public long Declined { get; set; }
        public long Offline { get; set; }
        public long Conflicted { get; set; }
        public long Volume { get; set; }
        public long Amount { get; set; }
    }

    /// <summary>Every day from <paramref name="from"/> to <paramref name="to"/>, inclusive, quiet days included as zeros.</summary>
    public async Task<Result<IReadOnlyList<ActivityDay>>> DaysAsync(DateOnly? from, DateOnly? to, ActivitySubject subject, CancellationToken ct)
    {
        if (from is not { } first || to is not { } last || last < first)
        {
            return Result<IReadOnlyList<ActivityDay>>.Fail(400, ErrorCodes.ValidationFailed, "from and to are required, to not before from", "from");
        }
        if (last.DayNumber - first.DayNumber >= MaxDays)
        {
            return Result<IReadOnlyList<ActivityDay>>.Fail(400, "EXPORT_RANGE_TOO_LONG", "At most a year at a time", "to",
                new Dictionary<string, object> { ["maxDays"] = MaxDays });
        }
        var start = Periods.AtStartOfDay(first, Cairo);
        var end = Periods.AtStartOfDay(last.AddDays(1), Cairo);
        var args = new
        {
            start = start.UtcDateTime,
            end = end.UtcDateTime,
            company = subject.CompanyId,
            @operator = subject.OperatorId,
            station = subject.StationId,
            vehicle = subject.VehicleId,
            device = subject.DeviceId,
            agent = subject.AgentId,
        };

        var byDay = new Dictionary<DateOnly, long[]>();
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            byDay[day] = new long[7];
        }
        var tx = session.Current;
        foreach (var row in await tx.QueryAsync<DayRow>("""
            select (evaluated_at at time zone 'Africa/Cairo')::date as day,
                   count(*) filter (where status = 'COMPLETED') as completed,
                   count(*) filter (where status = 'DECLINED') as declined,
                   count(*) filter (where origin = 'OFFLINE') as offline,
                   count(*) filter (where conflicts <> '{}') as conflicted,
                   coalesce(sum(volume_ml) filter (where status = 'COMPLETED'), 0)::bigint as volume,
                   coalesce(sum(amount_piastres) filter (where status = 'COMPLETED'), 0)::bigint as amount
            from fuelings
            where evaluated_at >= @start and evaluated_at < @end
              and (cast(@company as uuid) is null or company_id = @company)
              and (cast(@operator as uuid) is null or operator_id = @operator)
              and (cast(@station as uuid) is null or station_id = @station)
              and (cast(@vehicle as uuid) is null or vehicle_id = @vehicle)
              and (cast(@device as uuid) is null or device_id = @device)
              and (cast(@agent as uuid) is null or agent_id = @agent)
            group by 1
            """, args, ct))
        {
            if (byDay.TryGetValue(row.Day, out var counts))
            {
                counts[0] = row.Completed;
                counts[1] = row.Declined;
                counts[2] = row.Offline;
                counts[3] = row.Conflicted;
                counts[4] = row.Volume;
                counts[5] = row.Amount;
            }
        }
        // Alerts have no operator or agent column; for those subjects they are counted through the station or device they
        // name. Row-level security hides what the caller may not see.
        foreach (var row in await tx.QueryAsync<AlertRow>("""
            select (created_at at time zone 'Africa/Cairo')::date as day, count(*) as alerts
            from alerts
            where created_at >= @start and created_at < @end
              and (cast(@company as uuid) is null or company_id = @company)
              and (cast(@operator as uuid) is null or operator_id = @operator)
              and (cast(@station as uuid) is null or station_id = @station)
              and (cast(@vehicle as uuid) is null or vehicle_id = @vehicle)
              and (cast(@device as uuid) is null or device_id = @device)
              and cast(@agent as uuid) is null
            group by 1
            """, args, ct))
        {
            if (byDay.TryGetValue(row.Day, out var counts))
            {
                counts[6] = row.Alerts;
            }
        }
        return Result<IReadOnlyList<ActivityDay>>.Ok(byDay.Select(d => new ActivityDay(d.Key, (int)d.Value[0], (int)d.Value[1],
            (int)d.Value[2], (int)d.Value[3], d.Value[4], d.Value[5], (int)d.Value[6])).ToList());
    }
}
