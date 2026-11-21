using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Sync;

/// <summary>The last batch a device sent (Java <c>SyncMonitor.LastSyncBatch</c>).</summary>
public sealed record LastSyncBatch(DateTimeOffset ReceivedAt, int Items, int Recorded, int Duplicates, int Rejected, int WithConflicts, int Pending);

/// <summary>An active device with its last contact and last batch (Java <c>SyncMonitor.DeviceSync</c>).</summary>
public sealed record DeviceSync(Guid DeviceId, string Code, string? Label, Guid OperatorId, Guid StationId, string StationNameAr,
    string StationNameEn, DateTimeOffset? LastSyncAt, bool Stale, LastSyncBatch? LastBatch);

/// <summary>A batch received from a device (Java <c>SyncMonitor.SyncBatch</c>).</summary>
public sealed record SyncBatch(Guid Id, Guid DeviceBatchId, Guid DeviceId, Guid StationId, DateTimeOffset ReceivedAt, int Items, int Recorded,
    int Duplicates, int Rejected, int WithConflicts, int Pending);

/// <summary>
/// The sync monitor (B5 contract §5; Java <c>SyncMonitor</c>): when each active device last reached the server, what its last
/// batch did, and what it said it still holds. Runs in the caller's scope: Operations sees every device, a station manager
/// the operator's own (row-level security on devices and batches).
/// </summary>
public sealed class SyncMonitor(IDbSession session, TimeProvider clock)
{
    /// <summary>A device not heard from for longer than this is flagged.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    private DbTx Tx => session.Current;

    private sealed class DeviceRow
    {
        public Guid Id { get; set; }
        public string Code { get; set; } = "";
        public string? Label { get; set; }
        public Guid Operator_Id { get; set; }
        public Guid Station_Id { get; set; }
        public string Name_Ar { get; set; } = "";
        public string Name_En { get; set; } = "";
        public DateTime? Last_Sync_At { get; set; }
        public DateTime? Received_At { get; set; }
        public int? Item_Count { get; set; }
        public int? Recorded { get; set; }
        public int? Duplicates { get; set; }
        public int? Rejected { get; set; }
        public int? With_Conflicts { get; set; }
        public int? Pending_Reported { get; set; }
    }

    private sealed class BatchRow
    {
        public Guid Id { get; set; }
        public Guid Device_Batch_Id { get; set; }
        public Guid Device_Id { get; set; }
        public Guid Station_Id { get; set; }
        public DateTime Received_At { get; set; }
        public int Item_Count { get; set; }
        public int Recorded { get; set; }
        public int Duplicates { get; set; }
        public int Rejected { get; set; }
        public int With_Conflicts { get; set; }
        public int Pending_Reported { get; set; }
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    /// <summary>Active devices, newest first by id (Java's order), with the last batch of each.</summary>
    public async Task<Page<DeviceSync>> DevicesAsync(Guid? operatorId, Guid? stationId, bool? staleOnly, string? cursor, int? limit,
        CancellationToken ct)
    {
        var size = Page<DeviceSync>.Limit(limit);
        var staleBefore = clock.GetUtcNow() - StaleAfter;
        var rows = await Tx.QueryAsync<DeviceRow>("""
            select d.id, d.code, d.label, d.operator_id, d.station_id, s.name_ar, s.name_en, d.last_sync_at,
                   b.received_at, b.item_count, b.recorded, b.duplicates, b.rejected, b.with_conflicts,
                   b.pending_reported
            from pos_devices d
            join stations s on s.id = d.station_id
            left join lateral (
              select * from sync_batches sb where sb.device_id = d.id order by sb.id desc limit 1
            ) b on true
            where d.status = 'ACTIVE'
              and (cast(@operator as uuid) is null or d.operator_id = @operator)
              and (cast(@station as uuid) is null or d.station_id = @station)
              and (not @staleOnly or d.last_sync_at is null or d.last_sync_at < @staleBefore)
              and (cast(@cursor as uuid) is null or d.id < @cursor)
            order by d.id desc limit @limit
            """, new
        {
            @operator = operatorId,
            station = stationId,
            staleOnly = staleOnly == true,
            staleBefore = staleBefore.UtcDateTime,
            cursor = Cursors.Parse(cursor),
            limit = size + 1,
        }, ct);
        var devices = rows.Select(r =>
        {
            DateTimeOffset? lastSync = r.Last_Sync_At is { } seen ? Utc(seen) : null;
            var last = r.Received_At is { } at
                ? new LastSyncBatch(Utc(at), r.Item_Count!.Value, r.Recorded!.Value, r.Duplicates!.Value, r.Rejected!.Value,
                    r.With_Conflicts!.Value, r.Pending_Reported!.Value)
                : null;
            return new DeviceSync(r.Id, r.Code, r.Label, r.Operator_Id, r.Station_Id, r.Name_Ar, r.Name_En, lastSync,
                lastSync is null || lastSync < staleBefore, last);
        }).ToList();
        return Page<DeviceSync>.Of(devices, size, d => d.DeviceId);
    }

    public async Task<Page<SyncBatch>> BatchesAsync(Guid? operatorId, Guid? deviceId, string? cursor, int? limit, CancellationToken ct)
    {
        var size = Page<SyncBatch>.Limit(limit);
        var rows = await Tx.QueryAsync<BatchRow>("""
            select * from sync_batches
            where (cast(@operator as uuid) is null or operator_id = @operator)
              and (cast(@device as uuid) is null or device_id = @device)
              and (cast(@cursor as uuid) is null or id < @cursor)
            order by id desc limit @limit
            """, new { @operator = operatorId, device = deviceId, cursor = Cursors.Parse(cursor), limit = size + 1 }, ct);
        return Page<SyncBatch>.Of(rows.Select(r => new SyncBatch(r.Id, r.Device_Batch_Id, r.Device_Id, r.Station_Id, Utc(r.Received_At),
            r.Item_Count, r.Recorded, r.Duplicates, r.Rejected, r.With_Conflicts, r.Pending_Reported)).ToList(), size, b => b.Id);
    }
}
