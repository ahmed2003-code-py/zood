using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Network;

/// <summary>
/// Shifts (plan §7 B6; Java <c>network.Shifts</c>): an agent's session on a device, from sign-in to sign-out, revocation
/// or expiry. They are what "last shift" means in the quick period filters and on the calendar. Read in the caller's
/// scope: Operations sees all, a station manager the operator's own (V10).
/// </summary>
public sealed class Shifts(IDbSession session, TimeProvider clock)
{
    private sealed class ShiftRow
    {
        public Guid Id { get; set; }
        public Guid Agent_Id { get; set; }
        public string Worker_Id { get; set; } = "";
        public string Full_Name { get; set; } = "";
        public Guid Device_Id { get; set; }
        public string Code { get; set; } = "";
        public Guid Station_Id { get; set; }
        public DateTime Created_At { get; set; }
        public DateTime? Revoked_At { get; set; }
        public string? Revoke_Reason { get; set; }
        public DateTime Expires_At { get; set; }
    }

    /// <summary>Shifts newest first, filtered by operator, station, device or agent.</summary>
    public async Task<Result<Page<Shift>>> ListAsync(Guid? operatorId, Guid? stationId, Guid? deviceId, Guid? agentId, string? cursor,
        int? limit, CancellationToken ct)
    {
        var size = Page<Shift>.Limit(limit);
        var now = clock.GetUtcNow();
        var rows = (await session.Current.QueryAsync<ShiftRow>("""
            select s.id, s.agent_id, a.worker_id, a.full_name, s.device_id, d.code, s.station_id, s.created_at,
                   s.revoked_at, s.revoke_reason, s.expires_at
            from agent_sessions s
            join agents a on a.id = s.agent_id
            join pos_devices d on d.id = s.device_id
            where (cast(@operator as uuid) is null or s.operator_id = @operator)
              and (cast(@station as uuid) is null or s.station_id = @station)
              and (cast(@device as uuid) is null or s.device_id = @device)
              and (cast(@agent as uuid) is null or s.agent_id = @agent)
              and (cast(@cursor as uuid) is null or s.id < @cursor)
            order by s.created_at desc, s.id desc limit @limit
            """, new
        {
            @operator = operatorId, station = stationId, device = deviceId, agent = agentId, cursor = Cursors.Parse(cursor), limit = size + 1,
        }, ct)).Select(r =>
        {
            var revoked = Rows.Utc(r.Revoked_At);
            var expires = Rows.Utc(r.Expires_At);
            var ended = revoked ?? (expires > now ? (DateTimeOffset?)null : expires);
            var reason = revoked is not null ? r.Revoke_Reason : ended is not null ? "EXPIRED" : null;
            return new Shift(r.Id, r.Agent_Id, r.Worker_Id, r.Full_Name, r.Device_Id, r.Code, r.Station_Id, Rows.Utc(r.Created_At), ended, reason);
        }).ToList();
        return Page<Shift>.Of(rows, size, s => s.Id);
    }
}
