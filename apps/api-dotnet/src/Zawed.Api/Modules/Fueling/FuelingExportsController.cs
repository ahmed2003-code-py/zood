using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Fueling;

/// <summary>
/// Fuelings as a CSV file, for each audience the same rows its list shows (B6, roadmap delta: transaction export; Java
/// <c>FuelingExportsController</c>). The range is two Cairo calendar days, inclusive: <c>from=2026-11-01&amp;to=2026-11-30</c>.
/// </summary>
[Route("api/v1")]
public sealed class FuelingExportsController(FuelingExports exports) : ApiControllerBase
{
    private Guid ScopeId => ZawedPrincipal.From(User)!.Scope.Id!.Value;

    /// <summary>Every fueling in a date range as CSV (Cairo days, inclusive).</summary>
    [HttpGet("ops/fuelings/export", Name = "exportFuelings"), Tags("fuelings"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<byte[]>(200)]
    public Task<IActionResult> Ops([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] Guid? companyId,
        [FromQuery] Guid? operatorId, [FromQuery] Guid? stationId, [FromQuery] Guid? vehicleId, [FromQuery] string? status,
        CancellationToken ct, [FromQuery] string lang = "en") =>
        FileAsync(from, to, companyId, operatorId, stationId, vehicleId, status, lang, ct);

    /// <summary>The company's fuelings in a date range as CSV.</summary>
    [HttpGet("company/fuelings/export", Name = "exportMyFuelings"), Tags("company portal"), RequiresPermission(Permission.COMPANY_READ)]
    [ProducesResponseType<byte[]>(200)]
    public Task<IActionResult> Company([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] Guid? vehicleId,
        [FromQuery] string? status, CancellationToken ct, [FromQuery] string lang = "en") =>
        FileAsync(from, to, ScopeId, null, null, vehicleId, status, lang, ct);

    /// <summary>Fuelings at the operator's stations in a date range as CSV.</summary>
    [HttpGet("station/fuelings/export", Name = "exportStationFuelings"), Tags("station portal"), RequiresPermission(Permission.STATION_READ)]
    [ProducesResponseType<byte[]>(200)]
    public Task<IActionResult> Station([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] Guid? stationId,
        [FromQuery] string? status, CancellationToken ct, [FromQuery] string lang = "en") =>
        FileAsync(from, to, null, ScopeId, stationId, null, status, lang, ct);

    private async Task<IActionResult> FileAsync(DateOnly? from, DateOnly? to, Guid? companyId, Guid? operatorId, Guid? stationId,
        Guid? vehicleId, string? status, string lang, CancellationToken ct)
    {
        var range = FuelingExports.Range(from, to);
        if (!range.IsSuccess)
        {
            return From(range);
        }
        var csv = await exports.CsvAsync(range.Data!, companyId, operatorId, stationId, vehicleId, status, lang == "ar", ct);
        if (!csv.IsSuccess)
        {
            return From(csv);
        }
        var name = "zawed-fuelings-" + Day(from) + "-" + Day(to) + ".csv";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{name}\"";
        Response.Headers.CacheControl = "no-store";
        return File(csv.Data!, "text/csv;charset=UTF-8");
    }

    private static string Day(DateOnly? day) => day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "null";
}
