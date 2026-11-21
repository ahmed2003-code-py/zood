using Microsoft.AspNetCore.Mvc;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Network;

/// <summary>Station operators, stations, POS devices and agents in the Ops console (PRD §6.3; Java <c>NetworkController</c>).</summary>
[Route("api/v1/ops"), Tags("network")]
public sealed class NetworkController(Operators operators, Devices devices, Agents agents) : ApiControllerBase
{
    private ZawedPrincipal Actor => ZawedPrincipal.From(User)!;

    // ----------------------------------------------------------------------------------------------- operators

    [HttpGet("operators", Name = "listOperators"), RequiresPermission(Permission.OPS_READ)]
    [EndpointSummary("Station operators, newest first")]
    [ProducesResponseType<Page<OperatorResponse>>(200)]
    public async Task<IActionResult> ListOperators([FromQuery] OperatorStatus? status, [FromQuery] string? q, [FromQuery] string? cursor,
        [FromQuery] int? limit, CancellationToken ct) => From(await operators.ListAsync(status, q, cursor, limit, ct));

    [HttpPost("operators", Name = "createOperator"), RequiresPermission(Permission.NETWORK_MANAGE)]
    [EndpointSummary("Onboard a station operator")]
    [ProducesResponseType<OperatorResponse>(201)]
    public async Task<IActionResult> CreateOperator([FromBody] OperatorBody body, CancellationToken ct) =>
        From(await operators.CreateAsync(Actor, body, ct));

    [HttpGet("operators/{id}", Name = "getOperator"), RequiresPermission(Permission.OPS_READ)]
    [EndpointSummary("One station operator")]
    [ProducesResponseType<OperatorResponse>(200)]
    public async Task<IActionResult> GetOperator(Guid id, CancellationToken ct) => From(await operators.GetAsync(id, ct));

    [HttpPut("operators/{id}", Name = "updateOperator"), RequiresPermission(Permission.NETWORK_MANAGE)]
    [EndpointSummary("Edit a station operator")]
    [ProducesResponseType<OperatorResponse>(200)]
    public async Task<IActionResult> UpdateOperator(Guid id, [FromBody] OperatorBody body, CancellationToken ct) =>
        From(await operators.UpdateAsync(Actor, id, body, ct));

    [HttpPost("operators/{id}/status", Name = "setOperatorStatus"), RequiresPermission(Permission.NETWORK_MANAGE)]
    [EndpointSummary("Suspend or reactivate a station operator")]
    [ProducesResponseType<OperatorResponse>(200)]
    public async Task<IActionResult> SetOperatorStatus(Guid id, [FromBody] OperatorStatusRequest body, CancellationToken ct) =>
        From(await operators.SetStatusAsync(Actor, id, body.Status!.Value, Rows.JavaTrim(body.Reason), ct));

    // ------------------------------------------------------------------------------------------------ stations

    [HttpGet("stations", Name = "listStations"), RequiresPermission(Permission.OPS_READ)]
    [EndpointSummary("Stations, newest first")]
    [ProducesResponseType<Page<StationResponse>>(200)]
    public async Task<IActionResult> ListStations([FromQuery] Guid? operatorId, [FromQuery] StationStatus? status, [FromQuery] string? q,
        [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct) =>
        From(await operators.StationsAsync(operatorId, status, q, cursor, limit, ct));

    [HttpPost("stations", Name = "createStation"), RequiresPermission(Permission.NETWORK_MANAGE)]
    [EndpointSummary("Add a station to an operator")]
    [ProducesResponseType<StationResponse>(201)]
    public async Task<IActionResult> CreateStation([FromBody] CreateStationRequest body, CancellationToken ct) =>
        From(await operators.CreateStationAsync(Actor, body.OperatorId!.Value, body.Station!, ct));

    [HttpGet("stations/{id}", Name = "getStation"), RequiresPermission(Permission.OPS_READ)]
    [EndpointSummary("One station")]
    [ProducesResponseType<StationResponse>(200)]
    public async Task<IActionResult> GetStation(Guid id, CancellationToken ct) => From(await operators.StationAsync(id, ct));

    [HttpPut("stations/{id}", Name = "updateStation"), RequiresPermission(Permission.NETWORK_MANAGE)]
    [EndpointSummary("Edit a station")]
    [ProducesResponseType<StationResponse>(200)]
    public async Task<IActionResult> UpdateStation(Guid id, [FromBody] StationBody body, CancellationToken ct) =>
        From(await operators.UpdateStationAsync(Actor, id, body, ct));

    [HttpPost("stations/{id}/status", Name = "setStationStatus"), RequiresPermission(Permission.NETWORK_MANAGE)]
    [EndpointSummary("Suspend, reactivate or close a station")]
    [ProducesResponseType<StationResponse>(200)]
    public async Task<IActionResult> SetStationStatus(Guid id, [FromBody] StationStatusRequest body, CancellationToken ct) =>
        From(await operators.SetStationStatusAsync(Actor, id, body.Status!.Value, Rows.JavaTrim(body.Reason), ct));

    // ------------------------------------------------------------------------------------------------- devices

    [HttpGet("devices", Name = "listDevices"), RequiresPermission(Permission.OPS_READ)]
    [EndpointSummary("POS devices, newest first")]
    [ProducesResponseType<Page<DeviceResponse>>(200)]
    public async Task<IActionResult> ListDevices([FromQuery] Guid? operatorId, [FromQuery] Guid? stationId, [FromQuery] DeviceStatus? status,
        [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct) =>
        From(await devices.ListAsync(operatorId, stationId, status, cursor, limit, ct));

    /// <summary>Java documents 200 although a new device answers 201.</summary>
    [HttpPost("devices", Name = "registerDevice"), RequiresPermission(Permission.NETWORK_MANAGE)]
    [EndpointSummary("Register a POS device; the activation code is shown once")]
    [ProducesResponseType<DeviceActivationResponse>(200)]
    public async Task<IActionResult> RegisterDevice([FromBody] RegisterDeviceRequest body, CancellationToken ct) =>
        Secret(await devices.RegisterAsync(Actor, body.StationId!.Value, body.Label, ct), r => new DeviceActivationResponse(r.Device, r.ActivationCode));

    [HttpGet("devices/{id}", Name = "getDevice"), RequiresPermission(Permission.OPS_READ)]
    [EndpointSummary("One POS device")]
    [ProducesResponseType<DeviceResponse>(200)]
    public async Task<IActionResult> GetDevice(Guid id, CancellationToken ct) => From(await devices.GetAsync(id, ct));

    [HttpPost("devices/{id}/activation-code", Name = "reissueDeviceActivation"), RequiresPermission(Permission.NETWORK_MANAGE)]
    [EndpointSummary("A new activation code for a device not yet activated")]
    [ProducesResponseType<DeviceActivationResponse>(200)]
    public async Task<IActionResult> ReissueActivation(Guid id, CancellationToken ct) =>
        Secret(await devices.ReissueActivationAsync(Actor, id, ct), r => new DeviceActivationResponse(r.Device, r.ActivationCode));

    [HttpPost("devices/{id}/revoke", Name = "revokeDevice"), RequiresPermission(Permission.NETWORK_MANAGE)]
    [EndpointSummary("Revoke a device for good")]
    [ProducesResponseType<DeviceResponse>(200)]
    public async Task<IActionResult> RevokeDevice(Guid id, [FromBody] RevokeDeviceRequest body, CancellationToken ct) =>
        From(await devices.RevokeAsync(Actor, id, Rows.JavaTrim(body.Reason), ct));

    // -------------------------------------------------------------------------------------------------- agents

    [HttpGet("agents", Name = "listAgents"), RequiresPermission(Permission.OPS_READ)]
    [EndpointSummary("Station agents, newest first; q matches the name or worker id")]
    [ProducesResponseType<Page<AgentResponse>>(200)]
    public async Task<IActionResult> ListAgents([FromQuery] Guid? operatorId, [FromQuery] Guid? stationId, [FromQuery] AgentStatus? status,
        [FromQuery] string? q, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct) =>
        From(await agents.ListAsync(operatorId, stationId, status, q, cursor, limit, ct));

    /// <summary>Java documents 200 although a new agent answers 201.</summary>
    [HttpPost("agents", Name = "createAgent"), RequiresPermission(Permission.NETWORK_MANAGE)]
    [EndpointSummary("Add a station agent; the PIN is shown once")]
    [ProducesResponseType<AgentPinResponse>(200)]
    public async Task<IActionResult> CreateAgent([FromBody] CreateAgentRequest body, CancellationToken ct) =>
        Secret(await agents.CreateAsync(Actor, body.StationId!.Value, body.FullName, body.Phone, ct), r => new AgentPinResponse(r.Agent, r.Pin));

    [HttpGet("agents/{id}", Name = "getAgent"), RequiresPermission(Permission.OPS_READ)]
    [EndpointSummary("One station agent")]
    [ProducesResponseType<AgentResponse>(200)]
    public async Task<IActionResult> GetAgent(Guid id, CancellationToken ct) => From(await agents.GetAsync(id, ct));

    [HttpPost("agents/{id}/reset-pin", Name = "resetAgentPin"), RequiresPermission(Permission.NETWORK_MANAGE)]
    [EndpointSummary("A new PIN, shown once; clears any lockout")]
    [ProducesResponseType<AgentPinResponse>(200)]
    public async Task<IActionResult> ResetPin(Guid id, CancellationToken ct) =>
        Secret(await agents.ResetPinAsync(Actor, id, ct), r => new AgentPinResponse(r.Agent, r.Pin));

    [HttpPost("agents/{id}/status", Name = "setAgentStatus"), RequiresPermission(Permission.NETWORK_MANAGE)]
    [EndpointSummary("Suspend, reactivate or deactivate an agent")]
    [ProducesResponseType<AgentResponse>(200)]
    public async Task<IActionResult> SetAgentStatus(Guid id, [FromBody] AgentStatusRequest body, CancellationToken ct) =>
        From(await agents.SetStatusAsync(Actor, id, body.Status!.Value, Rows.JavaTrim(body.Reason), ct));

    /// <summary>A response that carries a one-time secret must never be stored by a browser or a proxy.</summary>
    private IActionResult Secret<T, TBody>(Result<T> result, Func<T, TBody> body)
    {
        if (!result.IsSuccess)
        {
            return new ApiErrorResult(result.Status, result.Error!);
        }
        Response.Headers.CacheControl = "no-store";
        return StatusCode(result.Status, body(result.Data!));
    }
}

/// <summary>
/// The station portal's view of its own operator, read-only (Java <c>StationPortalController</c>). The operator comes from
/// the signed-in user's scope, and row-level security hides every other operator's rows.
/// </summary>
[Route("api/v1/station"), Tags("station portal")]
public sealed class StationPortalController(Operators operators, Devices devices, Agents agents, Shifts shifts) : ApiControllerBase
{
    private Guid OperatorId => ZawedPrincipal.From(User)!.Scope.Id!.Value;

    [HttpGet("operator", Name = "getMyOperator"), RequiresPermission(Permission.STATION_READ)]
    [EndpointSummary("The signed-in user's station operator")]
    [ProducesResponseType<OperatorResponse>(200)]
    public async Task<IActionResult> Operator(CancellationToken ct) => From(await operators.GetAsync(OperatorId, ct));

    [HttpGet("stations", Name = "listMyStations"), RequiresPermission(Permission.STATION_READ)]
    [EndpointSummary("The operator's stations")]
    [ProducesResponseType<Page<StationResponse>>(200)]
    public async Task<IActionResult> Stations([FromQuery] StationStatus? status, [FromQuery] string? q, [FromQuery] string? cursor,
        [FromQuery] int? limit, CancellationToken ct) => From(await operators.StationsAsync(OperatorId, status, q, cursor, limit, ct));

    [HttpGet("stations/{id}", Name = "getMyStation"), RequiresPermission(Permission.STATION_READ)]
    [EndpointSummary("One of the operator's stations")]
    [ProducesResponseType<StationResponse>(200)]
    public async Task<IActionResult> Station(Guid id, CancellationToken ct) => From(await operators.StationAsync(id, ct));

    [HttpGet("devices", Name = "listMyDevices"), RequiresPermission(Permission.STATION_READ)]
    [EndpointSummary("The operator's POS devices")]
    [ProducesResponseType<Page<DeviceResponse>>(200)]
    public async Task<IActionResult> Devices([FromQuery] Guid? stationId, [FromQuery] DeviceStatus? status, [FromQuery] string? cursor,
        [FromQuery] int? limit, CancellationToken ct) => From(await devices.ListAsync(OperatorId, stationId, status, cursor, limit, ct));

    [HttpGet("agents", Name = "listMyAgents"), RequiresPermission(Permission.STATION_READ)]
    [EndpointSummary("The operator's agents")]
    [ProducesResponseType<Page<AgentResponse>>(200)]
    public async Task<IActionResult> Agents([FromQuery] Guid? stationId, [FromQuery] AgentStatus? status, [FromQuery] string? q,
        [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct) =>
        From(await agents.ListAsync(OperatorId, stationId, status, q, cursor, limit, ct));

    [HttpGet("shifts", Name = "listStationShifts"), RequiresPermission(Permission.STATION_READ)]
    [EndpointSummary("Shifts at the operator's stations, newest first")]
    [ProducesResponseType<Page<Shift>>(200)]
    public async Task<IActionResult> Shifts([FromQuery] Guid? stationId, [FromQuery] Guid? deviceId, [FromQuery] Guid? agentId,
        [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct) =>
        From(await shifts.ListAsync(OperatorId, stationId, deviceId, agentId, cursor, limit, ct));
}

/// <summary>Shifts in the Ops console (Java <c>ShiftsController.ops</c>).</summary>
[Route("api/v1/ops"), Tags("network")]
public sealed class ShiftsController(Shifts shifts) : ApiControllerBase
{
    [HttpGet("shifts", Name = "listShifts"), RequiresPermission(Permission.OPS_READ)]
    [EndpointSummary("Agents' shifts on devices, newest first")]
    [ProducesResponseType<Page<Shift>>(200)]
    public async Task<IActionResult> List([FromQuery] Guid? operatorId, [FromQuery] Guid? stationId, [FromQuery] Guid? deviceId,
        [FromQuery] Guid? agentId, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct) =>
        From(await shifts.ListAsync(operatorId, stationId, deviceId, agentId, cursor, limit, ct));
}
