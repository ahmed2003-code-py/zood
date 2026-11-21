using Microsoft.AspNetCore.Mvc;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;
using Zawed.Domain;

namespace Zawed.Api.Modules.Pricing;

/// <summary>National fuel prices and station overrides in the Ops console (Java <c>PricesController</c>).</summary>
[Route("api/v1/ops/prices"), Tags("pricing")]
public sealed class PricesController(Prices prices) : ApiControllerBase
{
    /// <summary>The price in force now for every fuel, nationally or at a station.</summary>
    [HttpGet("current", Name = "currentPrices"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<List<PriceResponse>>(200)]
    public async Task<IActionResult> Current([FromQuery] Guid? stationId, CancellationToken ct) =>
        From(await prices.CurrentAsync(stationId, ct));

    /// <summary>Price history and scheduled prices, newest first.</summary>
    [HttpGet(Name = "listPrices"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<Page<PriceEntryResponse>>(200)]
    public async Task<IActionResult> History([FromQuery] FuelType? fuelType, [FromQuery] Guid? stationId, [FromQuery] string? cursor,
        [FromQuery] int? limit, CancellationToken ct, [FromQuery] bool nationalOnly = false) =>
        From(await prices.HistoryAsync(fuelType, stationId, nationalOnly, cursor, limit, ct));

    /// <summary>Schedule a national price or a station override.</summary>
    [HttpPost(Name = "schedulePrice"), RequiresPermission(Permission.PRICES_MANAGE)]
    [ProducesResponseType<PriceEntryResponse>(201)]
    public async Task<IActionResult> Schedule([FromBody] ScheduleRequest body, CancellationToken ct) =>
        From(await prices.ScheduleAsync(ZawedPrincipal.From(User)!, body, ct));
}
