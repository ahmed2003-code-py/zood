using Microsoft.AspNetCore.Mvc;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Text;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Fleet;

/// <summary>Vehicles, RFID tags and limits in the Ops console (PRD §6.3; Java <c>FleetController</c>).</summary>
[Route("api/v1/ops"), Tags("fleet")]
public sealed class FleetController(Vehicles vehicles, Tags tags) : ApiControllerBase
{
    private ZawedPrincipal Actor => ZawedPrincipal.From(User)!;

    /// <summary>Vehicles, newest first; q matches the plate.</summary>
    [HttpGet("vehicles", Name = "listVehicles"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<Page<VehicleResponse>>(200)]
    public async Task<IActionResult> List([FromQuery] Guid? companyId, [FromQuery] Guid? departmentId, [FromQuery] VehicleStatus? status,
        [FromQuery] string? q, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct) =>
        From(await vehicles.ListAsync(companyId, departmentId, status, q, cursor, limit, forCompany: false, ct));

    /// <summary>Register a vehicle for a company.</summary>
    [HttpPost("vehicles", Name = "createVehicle"), RequiresPermission(Permission.FLEET_MANAGE)]
    [ProducesResponseType<VehicleResponse>(201)]
    public async Task<IActionResult> Create([FromBody] CreateVehicleRequest body, CancellationToken ct) =>
        From(await vehicles.CreateAsync(Actor, body.CompanyId!.Value, body.Vehicle!.Draft(), ct));

    /// <summary>A vehicle with its tags and active limits.</summary>
    [HttpGet("vehicles/{id}", Name = "getVehicle"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<VehicleDetail>(200)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => From(await vehicles.DetailAsync(id, forCompany: false, ct));

    /// <summary>Edit a vehicle.</summary>
    [HttpPut("vehicles/{id}", Name = "updateVehicle"), RequiresPermission(Permission.FLEET_MANAGE)]
    [ProducesResponseType<VehicleResponse>(200)]
    public async Task<IActionResult> Update(Guid id, [FromBody] VehicleBody body, CancellationToken ct) =>
        From(await vehicles.UpdateAsync(Actor, id, body.Draft(), ct));

    /// <summary>Suspend, reactivate or decommission a vehicle.</summary>
    [HttpPost("vehicles/{id}/status", Name = "setVehicleStatus"), RequiresPermission(Permission.FLEET_MANAGE)]
    [ProducesResponseType<VehicleResponse>(200)]
    public async Task<IActionResult> Status(Guid id, [FromBody] VehicleStatusRequest body, CancellationToken ct) =>
        From(await vehicles.SetStatusAsync(Actor, id, body.Status!.Value, JavaText.Trim(body.Reason), ct));

    /// <summary>Bind a new RFID tag to the vehicle.</summary>
    [HttpPost("vehicles/{id}/tags", Name = "issueTag"), RequiresPermission(Permission.FLEET_MANAGE)]
    [ProducesResponseType<TagResponse>(201)]
    public async Task<IActionResult> IssueTag(Guid id, [FromBody] IssueTagRequest body, CancellationToken ct) =>
        From(await tags.IssueAsync(Actor, id, body.Uid, ct));

    /// <summary>Report a tag lost or stolen, deactivate it, or flag it for replacement.</summary>
    [HttpPost("tags/{id}/status", Name = "setTagStatus"), RequiresPermission(Permission.FLEET_MANAGE)]
    [ProducesResponseType<TagResponse>(200)]
    public async Task<IActionResult> TagStatus(Guid id, [FromBody] TagStatusRequest body, CancellationToken ct) =>
        From(await tags.SetStatusAsync(Actor, id, body.Status!.Value, JavaText.Trim(body.Reason), ct));

    /// <summary>Set the limit for one kind and period, replacing the previous one.</summary>
    [HttpPut("vehicles/{id}/limits", Name = "setVehicleLimit"), RequiresPermission(Permission.FLEET_MANAGE)]
    [ProducesResponseType<List<LimitResponse>>(200)]
    public async Task<IActionResult> SetLimit(Guid id, [FromBody] LimitRequest body, CancellationToken ct) =>
        From(await vehicles.SetLimitAndListAsync(Actor, id, body.Kind!.Value, body.Period!.Value, body.MaxValue!.Value, ct));

    /// <summary>Retire a limit.</summary>
    [HttpDelete("vehicles/{id}/limits/{limitId}", Name = "removeVehicleLimit"), RequiresPermission(Permission.FLEET_MANAGE)]
    [ProducesResponseType(204)]
    public async Task<IActionResult> RemoveLimit(Guid id, Guid limitId, CancellationToken ct) =>
        From(await vehicles.RemoveLimitAsync(Actor, id, limitId, ct));
}

/// <summary>
/// The company portal's fleet, read-only (approved decision: changes go through requests to Operations; Java
/// <c>CompanyFleetController</c>). The company comes from the signed-in user's scope; row-level security hides every
/// other company's vehicles even if an id is guessed.
/// </summary>
[Route("api/v1/company/vehicles"), Tags("company portal")]
public sealed class CompanyFleetController(Vehicles vehicles) : ApiControllerBase
{
    private Guid CompanyId => ZawedPrincipal.From(User)!.Scope.Id!.Value;

    /// <summary>The company's vehicles; q matches the plate.</summary>
    [HttpGet(Name = "listMyVehicles"), RequiresPermission(Permission.COMPANY_READ)]
    [ProducesResponseType<Page<VehicleResponse>>(200)]
    public async Task<IActionResult> List([FromQuery] Guid? departmentId, [FromQuery] VehicleStatus? status, [FromQuery] string? q,
        [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct) =>
        From(await vehicles.ListAsync(CompanyId, departmentId, status, q, cursor, limit, forCompany: true, ct));

    /// <summary>One of the company's vehicles with its tags and limits.</summary>
    [HttpGet("{id}", Name = "getMyVehicle"), RequiresPermission(Permission.COMPANY_READ)]
    [ProducesResponseType<VehicleDetail>(200)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => From(await vehicles.DetailAsync(id, forCompany: true, ct));
}
