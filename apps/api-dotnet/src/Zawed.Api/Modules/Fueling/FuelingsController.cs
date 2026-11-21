using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zawed.Api.Modules.Network.Contracts;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Fueling;

/// <summary>
/// Fuelings (Java <c>FuelingsController</c>): the POS records one (plan §3.4); Operations, the company and the station read
/// them; Operations acknowledges hard alerts. Portal reads filter by the caller's own scope, and row-level security hides
/// every other tenant's rows anyway. The POS calls are authorised by the POS chain (<c>PosChannel</c>) and run in the
/// engine's own transactions.
/// </summary>
[Route("api/v1")]
public sealed class FuelingsController(FuelingEngine engine, Fuelings fuelings, Alerts alerts, Receipts receipts, OfflineSync sync)
    : ApiControllerBase
{
    private ZawedPrincipal Principal => ZawedPrincipal.From(User)!;

    private Guid ScopeId => Principal.Scope.Id!.Value;

    // -------------------------------------------------------------------------------------------------------- POS

    /// <summary>Decide and record a fueling; 201 when new, 200 with replayed=true for a clientTxnId seen before.</summary>
    [HttpPost("pos/fuelings", Name = "recordPosFueling"), Tags("pos"), OwnTransactions]
    [ProducesResponseType<PosFuelingResponse>(200)]
    public async Task<IActionResult> Record([FromBody] PosFuelingRequest body, CancellationToken ct)
    {
        var result = await engine.FuelAsync(PosPrincipal.From(User)!, body.ToRequest(), ct);
        Response.Headers.CacheControl = "no-store";
        return StatusCode(result.Replayed ? 200 : 201, PosFuelingResponse.Of(result, receipts.PathOf(result.Fueling)));
    }

    /// <summary>What became of an online fueling whose answer was lost: the stored fueling, or 404 if the server never got it.</summary>
    [HttpPost("pos/fuelings/{clientTxnId}/resolve", Name = "resolvePosFueling"), Tags("pos"), OwnTransactions]
    [ProducesResponseType<PosFuelingResponse>(200)]
    public async Task<IActionResult> Resolve(Guid clientTxnId, [FromBody] ResolveRequest body, CancellationToken ct)
    {
        var fueling = await engine.ResolveAsync(PosPrincipal.From(User)!, clientTxnId, body.ToldOutcome == "REFUSED", ct);
        Response.Headers.CacheControl = "no-store";
        return Ok(PosFuelingResponse.Of(new FuelingOutcome(fueling, true, null, null), receipts.PathOf(fueling)));
    }

    /// <summary>
    /// Send fuelings captured offline, at most 100; each is recorded on its own and answered in order as RECORDED, DUPLICATE or
    /// REJECTED. B5 contract §3: the answer is 200 whatever happened to the items; each has its own result, and a device that
    /// gets an answer may take every item in it off its queue.
    /// </summary>
    [HttpPost("pos/sync", Name = "syncPosFuelings"), Tags("pos"), OwnTransactions]
    [ProducesResponseType<PosSyncResponse>(200)]
    public async Task<IActionResult> Sync([FromBody] PosSyncRequest body)
    {
        var received = await sync.ReceiveAsync(PosPrincipal.From(User)!, body.ToBatch());
        var results = received.Results.Select(item => new PosSyncItemResult(item.ClientTxnId, item.Outcome, item.Code,
            item.Fueling is null ? null : PosSyncFueling.Of(item.Fueling, receipts.PathOf(item.Fueling)))).ToList();
        Response.Headers.CacheControl = "no-store";
        return Ok(new PosSyncResponse(received.BatchId, results));
    }

    // ---------------------------------------------------------------------------------------------------- receipt

    /// <summary>A fueling receipt behind its signed link.</summary>
    [HttpGet("receipts/{id}", Name = "getReceipt"), Tags("receipts"), AllowAnonymous, OwnTransactions]
    [ProducesResponseType<ReceiptResponse>(200)]
    public async Task<IActionResult> Receipt(Guid id, [FromQuery] string? sig, CancellationToken ct)
    {
        var fueling = await receipts.OpenAsync(id, sig, ClientInfo.Address(HttpContext), ct);
        Response.Headers.CacheControl = "no-store";
        return Ok(ReceiptResponse.Of(fueling));
    }

    // -------------------------------------------------------------------------------------------------------- Ops

    /// <summary>Every fueling, newest first.</summary>
    [HttpGet("ops/fuelings", Name = "listFuelings"), Tags("fuelings"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<Page<FuelingResponse>>(200)]
    public async Task<IActionResult> OpsList([FromQuery] Guid? companyId, [FromQuery] Guid? operatorId, [FromQuery] Guid? stationId,
        [FromQuery] Guid? vehicleId, [FromQuery] FuelingStatus? status, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct) =>
        Ok(FuelingResponse.PageOf(await fuelings.ListAsync(new FuelingFilter(companyId, operatorId, stationId, vehicleId, status, from, to),
            cursor, limit, ct)));

    /// <summary>One fueling.</summary>
    [HttpGet("ops/fuelings/{id}", Name = "getFueling"), Tags("fuelings"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<FuelingResponse>(200)]
    public async Task<IActionResult> OpsGet(Guid id, CancellationToken ct) =>
        From(await fuelings.FindFuelingAsync(id, ct) is { } fueling ? FuelingResponse.Of(fueling) : Result<FuelingResponse>.NotFound("Fueling"));

    /// <summary>Hard alerts, newest first.</summary>
    [HttpGet("ops/alerts", Name = "listAlerts"), Tags("alerts"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<Page<AlertResponse>>(200)]
    public async Task<IActionResult> OpsAlerts([FromQuery] Guid? companyId, [FromQuery] AlertStatus? status, [FromQuery] AlertType? type,
        [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct) =>
        Ok(AlertResponse.PageOf(await alerts.ListAsync(companyId, status, type, cursor, limit, ct)));

    /// <summary>Mark an alert as seen and handled.</summary>
    [HttpPost("ops/alerts/{id}/acknowledge", Name = "acknowledgeAlert"), Tags("alerts"), RequiresPermission(Permission.ALERTS_MANAGE)]
    [ProducesResponseType<AlertResponse>(200)]
    public async Task<IActionResult> Acknowledge(Guid id, CancellationToken ct)
    {
        var acknowledged = await alerts.AcknowledgeAsync(Principal, id, ct);
        return From(acknowledged.IsSuccess ? AlertResponse.Of(acknowledged.Data!) : Result<AlertResponse>.From(acknowledged));
    }

    // ---------------------------------------------------------------------------------------------------- company

    /// <summary>The company's fuelings, newest first.</summary>
    [HttpGet("company/fuelings", Name = "listMyFuelings"), Tags("company portal"), RequiresPermission(Permission.COMPANY_READ)]
    [ProducesResponseType<Page<FuelingResponse>>(200)]
    public async Task<IActionResult> CompanyList([FromQuery] Guid? vehicleId, [FromQuery] FuelingStatus? status, [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct) =>
        Ok(FuelingResponse.PageOf(await fuelings.ListAsync(new FuelingFilter(ScopeId, null, null, vehicleId, status, from, to), cursor, limit, ct)));

    /// <summary>One of the company's fuelings.</summary>
    [HttpGet("company/fuelings/{id}", Name = "getMyFueling"), Tags("company portal"), RequiresPermission(Permission.COMPANY_READ)]
    [ProducesResponseType<FuelingResponse>(200)]
    public async Task<IActionResult> CompanyGet(Guid id, CancellationToken ct) =>
        From(await fuelings.FindFuelingAsync(id, ct) is { } fueling && fueling.CompanyId == ScopeId
            ? FuelingResponse.Of(fueling)
            : Result<FuelingResponse>.NotFound("Fueling"));

    /// <summary>Alerts about the company's fleet and wallet, newest first.</summary>
    [HttpGet("company/alerts", Name = "listMyAlerts"), Tags("company portal"), RequiresPermission(Permission.COMPANY_READ)]
    [ProducesResponseType<Page<AlertResponse>>(200)]
    public async Task<IActionResult> CompanyAlerts([FromQuery] AlertStatus? status, [FromQuery] string? cursor, [FromQuery] int? limit,
        CancellationToken ct) =>
        Ok(AlertResponse.PageOf(await alerts.ListAsync(ScopeId, status, null, cursor, limit, ct)));

    // ---------------------------------------------------------------------------------------------------- station

    /// <summary>Fuelings at the operator's stations, newest first.</summary>
    [HttpGet("station/fuelings", Name = "listStationFuelings"), Tags("station portal"), RequiresPermission(Permission.STATION_READ)]
    [ProducesResponseType<Page<FuelingResponse>>(200)]
    public async Task<IActionResult> StationList([FromQuery] Guid? stationId, [FromQuery] FuelingStatus? status, [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct) =>
        Ok(FuelingResponse.PageOf(await fuelings.ListAsync(new FuelingFilter(null, ScopeId, stationId, null, status, from, to), cursor, limit, ct)));

    /// <summary>One fueling at the operator's stations.</summary>
    [HttpGet("station/fuelings/{id}", Name = "getStationFueling"), Tags("station portal"), RequiresPermission(Permission.STATION_READ)]
    [ProducesResponseType<FuelingResponse>(200)]
    public async Task<IActionResult> StationGet(Guid id, CancellationToken ct) =>
        From(await fuelings.FindFuelingAsync(id, ct) is { } fueling && fueling.OperatorId == ScopeId
            ? FuelingResponse.Of(fueling)
            : Result<FuelingResponse>.NotFound("Fueling"));
}
