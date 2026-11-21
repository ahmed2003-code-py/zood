using Microsoft.AspNetCore.Mvc;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Sync;

/// <summary>The sync monitor (B5 contract §5; Java <c>SyncMonitorController</c>).</summary>
[Route("api/v1")]
public sealed class SyncMonitorController(SyncMonitor monitor) : ApiControllerBase
{
    /// <summary>Active devices with their last sync and last batch.</summary>
    [HttpGet("ops/sync/devices", Name = "listSyncDevices"), Tags("sync"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<Page<DeviceSync>>(200)]
    public async Task<IActionResult> OpsDevices([FromQuery] Guid? operatorId, [FromQuery] Guid? stationId, [FromQuery] bool? stale,
        [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct) =>
        Ok(await monitor.DevicesAsync(operatorId, stationId, stale, cursor, limit, ct));

    /// <summary>Batches received from devices, newest first.</summary>
    [HttpGet("ops/sync/batches", Name = "listSyncBatches"), Tags("sync"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<Page<SyncBatch>>(200)]
    public async Task<IActionResult> OpsBatches([FromQuery] Guid? deviceId, [FromQuery] string? cursor, [FromQuery] int? limit,
        CancellationToken ct) =>
        Ok(await monitor.BatchesAsync(null, deviceId, cursor, limit, ct));

    /// <summary>The operator's active devices with their last sync.</summary>
    [HttpGet("station/sync/devices", Name = "listStationSyncDevices"), Tags("station portal"), RequiresPermission(Permission.STATION_READ)]
    [ProducesResponseType<Page<DeviceSync>>(200)]
    public async Task<IActionResult> StationDevices([FromQuery] Guid? stationId, [FromQuery] bool? stale, [FromQuery] string? cursor,
        [FromQuery] int? limit, CancellationToken ct) =>
        Ok(await monitor.DevicesAsync(ZawedPrincipal.From(User)!.Scope.Id, stationId, stale, cursor, limit, ct));
}
