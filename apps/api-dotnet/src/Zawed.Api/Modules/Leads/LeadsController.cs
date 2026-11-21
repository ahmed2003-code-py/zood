using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Leads;

/// <summary>
/// Demo requests from the public landing page, and the Ops list they land in (U1, u1-leads-contract.md; Java
/// <c>LeadsController</c>). The public half needs no identity; it is protected by <see cref="DemoRequestGate"/> (shared
/// per-address limits, a body cap) and by <see cref="Leads"/> (honeypot, time trap, per-contact limits, repeats).
/// </summary>
[Route("api/v1")]
public sealed class LeadsController(Leads leads) : ApiControllerBase
{
    /// <summary>
    /// Ask for a demo from the landing page. No sign-in. Limited per address (5 an hour, 20 a day; an IPv6 /48 20 an hour):
    /// 429 RATE_LIMITED with Retry-After past that. A body over 4 KiB is refused (413). The answer is 202 RECEIVED whether
    /// the request became a new lead, counted on an existing one, or was set aside (hidden field, timing, more than 3 a day
    /// per phone or e-mail, the form's hourly cap).
    /// </summary>
    [HttpPost("public/demo-requests", Name = "requestDemo"), Tags("public"), AllowAnonymous, OwnTransactions, DemoRequestGate]
    [ProducesResponseType<DemoRequestReceived>(202)]
    public async Task<IActionResult> RequestDemo([FromBody] DemoRequest body, CancellationToken ct)
    {
        await leads.SubmitAsync(new Leads.Submission(body.CompanyName!, body.ContactName!, body.Phone!, body.Email!, body.FleetSize!,
            body.Locale!, body.Website, body.StartedAt!.Value), ct);
        return StatusCode(202, DemoRequestReceived.Received);
    }

    /// <summary>Demo requests, newest first.</summary>
    [HttpGet("ops/leads", Name = "listLeads"), Tags("leads"), RequiresPermission(Permission.LEADS_MANAGE)]
    [ProducesResponseType<Page<LeadResponse>>(200)]
    public async Task<IActionResult> List([FromQuery] LeadStatus? status, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct) =>
        From(await leads.ListAsync(status, cursor, limit, ct));

    /// <summary>Move a lead along and keep a note on it.</summary>
    [HttpPatch("ops/leads/{id}", Name = "updateLead"), Tags("leads"), RequiresPermission(Permission.LEADS_MANAGE)]
    [ProducesResponseType<LeadResponse>(200)]
    public async Task<IActionResult> Update(Guid id, [FromBody] LeadUpdateRequest body, CancellationToken ct) =>
        From(await leads.UpdateAsync(ZawedPrincipal.From(User)!, id, body.Status!.Value, body.Note, ct));
}
