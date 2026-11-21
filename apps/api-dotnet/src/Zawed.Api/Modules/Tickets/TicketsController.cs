using Microsoft.AspNetCore.Mvc;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Tickets;

/// <summary>Support tickets for the company, the station and Operations (roadmap delta A5, plan §7 B6; Java <c>TicketsController</c>).</summary>
[Route("api/v1")]
public sealed class TicketsController(Tickets tickets) : ApiControllerBase
{
    private ZawedPrincipal Principal => ZawedPrincipal.From(User)!;

    // ------------------------------------------------------------------------------------------------------------ company

    [HttpGet("company/tickets", Name = "listMyTickets"), Tags("company portal"), RequiresPermission(Permission.COMPANY_READ)]
    [EndpointSummary("The company's tickets, newest first")]
    [ProducesResponseType<Page<TicketResponse>>(200)]
    public async Task<IActionResult> CompanyList([FromQuery] TicketStatus? status, [FromQuery] string? cursor, [FromQuery] int? limit,
        CancellationToken ct) =>
        From(await tickets.ListAsync(Principal.Scope.Id, null, status, null, cursor, limit, ct));

    [HttpPost("company/tickets", Name = "openMyTicket"), Tags("company portal")]
    [RequiresPermission(Permission.TICKETS_OPEN, Scopes = [ScopeType.COMPANY])]
    [EndpointSummary("Ask Operations for something: a lost tag, a limit change, a question")]
    [ProducesResponseType<TicketThreadResponse>(201)]
    public async Task<IActionResult> CompanyOpen([FromBody] OpenTicketRequest body, CancellationToken ct) =>
        WrongPortal(ScopeType.COMPANY) ?? From(await OpenAsync(body, ct));

    [HttpGet("company/tickets/{id}", Name = "getMyTicket"), Tags("company portal"), RequiresPermission(Permission.COMPANY_READ)]
    [EndpointSummary("One of the company's tickets with its thread")]
    [ProducesResponseType<TicketThreadResponse>(200)]
    public async Task<IActionResult> CompanyGet(Guid id, CancellationToken ct) => From(await tickets.ThreadAsync(id, false, ct));

    [HttpPost("company/tickets/{id}/messages", Name = "replyMyTicket"), Tags("company portal")]
    [RequiresPermission(Permission.TICKETS_OPEN, Scopes = [ScopeType.COMPANY])]
    [EndpointSummary("Add a message to one of the company's tickets")]
    [ProducesResponseType<TicketThreadResponse>(200)]
    public async Task<IActionResult> CompanyReply(Guid id, [FromBody] TicketReplyRequest body, CancellationToken ct) =>
        WrongPortal(ScopeType.COMPANY) ?? From(await tickets.ReplyAsync(Principal, id, body.Body, false, ct));

    // ------------------------------------------------------------------------------------------------------------ station

    [HttpGet("station/tickets", Name = "listStationTickets"), Tags("station portal"), RequiresPermission(Permission.STATION_READ)]
    [EndpointSummary("The operator's tickets, newest first")]
    [ProducesResponseType<Page<TicketResponse>>(200)]
    public async Task<IActionResult> StationList([FromQuery] TicketStatus? status, [FromQuery] string? cursor, [FromQuery] int? limit,
        CancellationToken ct) =>
        From(await tickets.ListAsync(null, Principal.Scope.Id, status, null, cursor, limit, ct));

    [HttpPost("station/tickets", Name = "openStationTicket"), Tags("station portal")]
    [RequiresPermission(Permission.TICKETS_OPEN, Scopes = [ScopeType.OPERATOR])]
    [EndpointSummary("Ask Operations for something: a device fault, a payout question")]
    [ProducesResponseType<TicketThreadResponse>(201)]
    public async Task<IActionResult> StationOpen([FromBody] OpenTicketRequest body, CancellationToken ct) =>
        WrongPortal(ScopeType.OPERATOR) ?? From(await OpenAsync(body, ct));

    [HttpGet("station/tickets/{id}", Name = "getStationTicket"), Tags("station portal"), RequiresPermission(Permission.STATION_READ)]
    [EndpointSummary("One of the operator's tickets with its thread")]
    [ProducesResponseType<TicketThreadResponse>(200)]
    public async Task<IActionResult> StationGet(Guid id, CancellationToken ct) => From(await tickets.ThreadAsync(id, false, ct));

    [HttpPost("station/tickets/{id}/messages", Name = "replyStationTicket"), Tags("station portal")]
    [RequiresPermission(Permission.TICKETS_OPEN, Scopes = [ScopeType.OPERATOR])]
    [EndpointSummary("Add a message to one of the operator's tickets")]
    [ProducesResponseType<TicketThreadResponse>(200)]
    public async Task<IActionResult> StationReply(Guid id, [FromBody] TicketReplyRequest body, CancellationToken ct) =>
        WrongPortal(ScopeType.OPERATOR) ?? From(await tickets.ReplyAsync(Principal, id, body.Body, false, ct));

    // ---------------------------------------------------------------------------------------------------------------- Ops

    [HttpGet("ops/tickets", Name = "listTickets"), Tags("tickets"), RequiresPermission(Permission.OPS_READ)]
    [EndpointSummary("Every ticket, newest first")]
    [ProducesResponseType<Page<TicketResponse>>(200)]
    public async Task<IActionResult> OpsList([FromQuery] Guid? companyId, [FromQuery] Guid? operatorId, [FromQuery] TicketStatus? status,
        [FromQuery] TicketType? type, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct) =>
        From(await tickets.ListAsync(companyId, operatorId, status, type, cursor, limit, ct));

    [HttpGet("ops/tickets/{id}", Name = "getTicket"), Tags("tickets"), RequiresPermission(Permission.OPS_READ)]
    [EndpointSummary("One ticket with its whole thread, internal notes included")]
    [ProducesResponseType<TicketThreadResponse>(200)]
    public async Task<IActionResult> OpsGet(Guid id, CancellationToken ct) => From(await tickets.ThreadAsync(id, true, ct));

    [HttpPost("ops/tickets/{id}/messages", Name = "replyTicket"), Tags("tickets"), RequiresPermission(Permission.TICKETS_MANAGE)]
    [EndpointSummary("Answer a ticket, or add an internal note")]
    [ProducesResponseType<TicketThreadResponse>(200)]
    public async Task<IActionResult> OpsReply(Guid id, [FromBody] TicketReplyRequest body, CancellationToken ct) =>
        From(await tickets.ReplyAsync(Principal, id, body.Body, body.Internal == true, ct));

    [HttpPost("ops/tickets/{id}/status", Name = "updateTicketStatus"), Tags("tickets"), RequiresPermission(Permission.TICKETS_MANAGE)]
    [EndpointSummary("Move a ticket along, and optionally take it")]
    [ProducesResponseType<TicketThreadResponse>(200)]
    public async Task<IActionResult> OpsStatus(Guid id, [FromBody] TicketStatusRequest body, CancellationToken ct) =>
        From(await tickets.UpdateAsync(Principal, id, body.Status!.Value, body.AssignToMe == true, ct));

    private Task<Result<TicketThreadResponse>> OpenAsync(OpenTicketRequest body, CancellationToken ct) =>
        tickets.OpenAsync(Principal, new Tickets.Draft(body.Type!.Value, body.Subject, body.Body, body.VehicleId, body.StationId, body.FuelingId),
            ct);

    /// <summary>
    /// TICKETS_OPEN is held by company and station users alike, so each portal's writes also check the caller's kind of scope: a
    /// station manager never writes through the company portal, or back. The permission's <c>Scopes</c> refuses first; this is
    /// Java's second check, kept as it was.
    /// </summary>
    private IActionResult? WrongPortal(ScopeType expected) =>
        Principal.Scope.Type != expected ? From(Result.Forbidden(message: "Not allowed for this portal")) : null;
}
