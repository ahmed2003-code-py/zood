using Microsoft.AspNetCore.Mvc;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Fueling;

/// <summary>The activity calendar for each audience (plan §7 B6; Java <c>ActivityController</c>). Days are Cairo days, inclusive.</summary>
[Route("api/v1")]
public sealed class ActivityController(Activity activity) : ApiControllerBase
{
    private Guid ScopeId => ZawedPrincipal.From(User)!.Scope.Id!.Value;

    /// <summary>What happened each day: the platform, or one company, operator, station, vehicle, device or agent.</summary>
    [HttpGet("ops/activity", Name = "getActivity"), Tags("fuelings"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<List<ActivityDay>>(200)]
    public async Task<IActionResult> Ops([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] Guid? companyId,
        [FromQuery] Guid? operatorId, [FromQuery] Guid? stationId, [FromQuery] Guid? vehicleId, [FromQuery] Guid? deviceId,
        [FromQuery] Guid? agentId, CancellationToken ct) =>
        From(await activity.DaysAsync(from, to, new ActivitySubject(companyId, operatorId, stationId, vehicleId, deviceId, agentId), ct));

    /// <summary>What happened each day to the company, or one of its vehicles.</summary>
    [HttpGet("company/activity", Name = "getMyActivity"), Tags("company portal"), RequiresPermission(Permission.COMPANY_READ)]
    [ProducesResponseType<List<ActivityDay>>(200)]
    public async Task<IActionResult> Company([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] Guid? vehicleId,
        CancellationToken ct) =>
        From(await activity.DaysAsync(from, to, new ActivitySubject(ScopeId, null, null, vehicleId, null, null), ct));

    /// <summary>What happened each day at the operator's stations, or one station, device or agent.</summary>
    [HttpGet("station/activity", Name = "getStationActivity"), Tags("station portal"), RequiresPermission(Permission.STATION_READ)]
    [ProducesResponseType<List<ActivityDay>>(200)]
    public async Task<IActionResult> Station([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] Guid? stationId,
        [FromQuery] Guid? deviceId, [FromQuery] Guid? agentId, CancellationToken ct) =>
        From(await activity.DaysAsync(from, to, new ActivitySubject(null, ScopeId, stationId, null, deviceId, agentId), ct));
}
