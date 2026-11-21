using Microsoft.AspNetCore.Mvc;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Tenancy;

/// <summary>Company onboarding and management in the Ops console (PRD §6.3; Java <c>CompaniesController</c>).</summary>
[Route("api/v1/ops/companies"), Tags("companies")]
public sealed class CompaniesController(Companies companies) : ApiControllerBase
{
    private ZawedPrincipal Actor => ZawedPrincipal.From(User)!;

    [HttpGet(Name = "listCompanies"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<Page<CompanyResponse>>(200)]
    public async Task<IActionResult> List([FromQuery] CompanyStatus? status, [FromQuery] string? q, [FromQuery] string? cursor,
        [FromQuery] int? limit, CancellationToken ct) =>
        From(await companies.ListAsync(status, q, cursor, limit, ct));

    /// <summary>
    /// Onboard a company. Send an Idempotency-Key so a retry or a double click answers the same company (200)
    /// instead of creating a second one.
    /// </summary>
    [HttpPost(Name = "createCompany"), RequiresPermission(Permission.COMPANIES_MANAGE)]
    [ProducesResponseType<CompanyResponse>(200)]
    public async Task<IActionResult> Create([FromHeader(Name = IdempotencyKeys.Header)] string? idempotencyKey,
        [FromBody] CreateCompanyRequest body, CancellationToken ct) =>
        From(await companies.CreateAsync(Actor, idempotencyKey, body, ct));

    [HttpGet("{id}", Name = "getCompany"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<CompanyResponse>(200)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => From(await companies.GetAsync(id, ct));

    [HttpPut("{id}", Name = "updateCompany"), RequiresPermission(Permission.COMPANIES_MANAGE)]
    [ProducesResponseType<CompanyResponse>(200)]
    public async Task<IActionResult> Update(Guid id, [FromBody] CompanyBody body, CancellationToken ct) =>
        From(await companies.UpdateAsync(Actor, id, body, ct));

    /// <summary>Cooldown window and low-balance threshold (Super Admin).</summary>
    [HttpPut("{id}/policy", Name = "updateCompanyPolicy"), RequiresPermission(Permission.COMPANY_POLICY_MANAGE)]
    [ProducesResponseType<CompanyResponse>(200)]
    public async Task<IActionResult> Policy(Guid id, [FromBody] CompanyPolicyRequest body, CancellationToken ct) =>
        From(await companies.UpdatePolicyAsync(Actor, id, body, ct));

    /// <summary>Freeze or unfreeze a company.</summary>
    [HttpPost("{id}/status", Name = "setCompanyStatus"), RequiresPermission(Permission.COMPANIES_MANAGE)]
    [ProducesResponseType<CompanyResponse>(200)]
    public async Task<IActionResult> Status(Guid id, [FromBody] CompanyStatusRequest body, CancellationToken ct) =>
        From(await companies.SetStatusAsync(Actor, id, body, ct));

    [HttpGet("{id}/departments", Name = "listCompanyDepartments"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<Page<DepartmentResponse>>(200)]
    public async Task<IActionResult> Departments(Guid id, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct) =>
        From(await companies.DepartmentsAsync(id, cursor, limit, ct));

    [HttpPost("{id}/departments", Name = "createDepartment"), RequiresPermission(Permission.COMPANIES_MANAGE)]
    [ProducesResponseType<DepartmentResponse>(201)]
    public async Task<IActionResult> AddDepartment(Guid id, [FromBody] CreateDepartmentRequest body, CancellationToken ct) =>
        From(await companies.AddDepartmentAsync(Actor, id, body, ct));
}

/// <summary>
/// The company portal's view of its own company (Java <c>CompanyPortalController</c>). The company id always comes
/// from the signed-in user's scope, never from the request, and row-level security would hide any other company
/// anyway. Read-only: changes go through requests to Operations.
/// </summary>
[Route("api/v1/company"), Tags("company portal")]
public sealed class CompanyPortalController(Companies companies) : ApiControllerBase
{
    private Guid CompanyId => ZawedPrincipal.From(User)!.Scope.Id!.Value;

    [HttpGet("profile", Name = "getMyCompany"), RequiresPermission(Permission.COMPANY_READ)]
    [ProducesResponseType<CompanyProfile>(200)]
    public async Task<IActionResult> Profile(CancellationToken ct) => From(await companies.ProfileAsync(CompanyId, ct));

    [HttpGet("departments", Name = "listMyDepartments"), RequiresPermission(Permission.COMPANY_READ)]
    [ProducesResponseType<Page<DepartmentResponse>>(200)]
    public async Task<IActionResult> Departments([FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct) =>
        From(await companies.DepartmentsAsync(CompanyId, cursor, limit, ct));
}
