using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Identity;

/// <summary>User management (contract §4; Java <c>AdminUsersController</c>). Each endpoint names its permission; the service adds Ops' limits.</summary>
[Route(ApiContract.BasePath + "/admin/users")]
[Tags("users")]
public sealed class AdminUsersController(UserAdmin users) : ApiControllerBase
{
    [HttpGet(Name = "listUsers"), RequiresPermission(Permission.USERS_READ)]
    [EndpointSummary("Users, newest first")]
    [ProducesResponseType<Page<AdminUserResponse>>(200)]
    public async Task<IActionResult> List([FromQuery] Role? role, [FromQuery] UserStatus? status, [FromQuery] string? q,
        [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct)
    {
        var page = await users.ListAsync(Principal, role, status, q, cursor, limit, ct);
        return page.IsSuccess
            ? Ok(new Page<AdminUserResponse>(page.Data!.Items.Select(AdminUserResponse.Of).ToList(), page.Data.NextCursor))
            : From(page);
    }

    [HttpPost(Name = "createUser"), RequiresPermission(Permission.USERS_CREATE)]
    [EndpointSummary("Create a user with a temporary password (shown once)")]
    [ProducesResponseType<CreatedUserResponse>(201)]
    public async Task<IActionResult> Create(CreateUserRequest body, CancellationToken ct)
    {
        Scope scope;
        try
        {
            scope = new Scope(body.Scope.Type!.Value, body.Scope.Id);
        }
        catch (ArgumentException ex)
        {
            return From(Result.Fail(400, "SCOPE_MISMATCH", ex.Message, "scope"));
        }
        var created = await users.CreateAsync(Principal, body.Email, body.FullName, body.Role!.Value, scope, ct);
        return created.IsSuccess
            ? StatusCode(201, new CreatedUserResponse(AdminUserResponse.Of(created.Data!.User), created.Data.TemporaryPassword))
            : From(created);
    }

    [HttpPost("{id}/reset-password", Name = "resetUserPassword"), RequiresPermission(Permission.USERS_RESET_PASSWORD)]
    [EndpointSummary("Issue a temporary password (shown once)")]
    [ProducesResponseType<TemporaryPasswordResponse>(200)]
    public async Task<IActionResult> ResetPassword(Guid id, CancellationToken ct)
    {
        var reset = await users.ResetPasswordAsync(Principal, id, ct);
        return reset.IsSuccess ? Ok(new TemporaryPasswordResponse(reset.Data!)) : From(reset);
    }

    [HttpPost("{id}/unlock", Name = "unlockUser"), RequiresPermission(Permission.USERS_UNLOCK)]
    [EndpointSummary("Clear a sign-in lockout")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> Unlock(Guid id, CancellationToken ct) => From(await users.UnlockAsync(Principal, id, ct));

    [HttpPost("{id}/status", Name = "setUserStatus"), RequiresPermission(Permission.USERS_STATUS)]
    [EndpointSummary("Suspend, deactivate or reactivate a user")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> Status(Guid id, StatusRequest body, CancellationToken ct) =>
        From(await users.SetStatusAsync(Principal, id, body.Status!.Value, Identity.SignIn.JavaTrim(body.Reason), ct));

    [HttpPost("{id}/mfa/reset", Name = "resetUserMfa"), RequiresPermission(Permission.USERS_MFA_RESET)]
    [EndpointSummary("Clear a user's second factor; the next sign-in re-enrols")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> ResetMfa(Guid id, ReasonRequest body, CancellationToken ct) =>
        From(await users.ResetMfaAsync(Principal, id, Identity.SignIn.JavaTrim(body.Reason), ct));

    private ZawedPrincipal Principal => ZawedPrincipal.From(User)!;
}

/// <summary>
/// Creates the first Super Admin, once (Java <c>SetupController</c>). Works only while <c>ZAWED_BOOTSTRAP_TOKEN</c> is
/// set and no Super Admin exists; afterwards it answers 404 like a route that does not exist.
/// </summary>
[Route(ApiContract.BasePath + "/setup")]
[Tags("auth")]
public sealed class SetupController(SignIn signIn) : ApiControllerBase
{
    [HttpPost("super-admin", Name = "bootstrapSuperAdmin"), AllowAnonymous, Platform.Persistence.OwnTransactions]
    [EndpointSummary("Create the first Super Admin (one time)")]
    [ProducesResponseType<UserResponse>(201)]
    public async Task<IActionResult> CreateSuperAdmin(SetupRequest body, CancellationToken ct) =>
        StatusCode(201, UserResponse.Of(await signIn.BootstrapAsync(body.SetupToken, body.Email, body.FullName, body.Password, HttpContext, ct)));
}
