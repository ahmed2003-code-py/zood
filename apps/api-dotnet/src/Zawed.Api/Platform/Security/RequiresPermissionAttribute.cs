using Microsoft.AspNetCore.Mvc.Filters;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Platform.Security;

/// <summary>
/// The permission an endpoint needs (Java <c>@RequiresPermission</c>). It is an authorisation filter, so
/// MVC runs it before model binding and validation: a caller without the permission gets the same
/// <c>403 FORBIDDEN</c> whatever the body says, and never a 400 that names the fields (the Java
/// <c>PermissionFirst</c> interceptor). <see cref="Scopes"/>, when given, also restricts the kind of scope
/// (for a permission several portals share, such as TICKETS_OPEN).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class RequiresPermissionAttribute(Permission permission) : Attribute, IAsyncAuthorizationFilter
{
    public Permission Permission { get; } = permission;

    public ScopeType[] Scopes { get; init; } = [];

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        // A method-level attribute overrides a class-level one, as AnnotatedElementUtils did.
        var nearest = context.ActionDescriptor.FilterDescriptors
            .Where(f => f.Filter is RequiresPermissionAttribute)
            .OrderByDescending(f => f.Scope)
            .Select(f => (RequiresPermissionAttribute)f.Filter)
            .First();
        if (!ReferenceEquals(nearest, this))
        {
            return Task.CompletedTask;
        }
        var principal = ZawedPrincipal.From(context.HttpContext.User);
        if (principal is null)
        {
            context.Result = new ApiErrorResult(401, new ResultError(ErrorCodes.Unauthenticated, "Authentication is required", null, null, null));
        }
        else if (!principal.Can(Permission) || (Scopes.Length > 0 && !Scopes.Contains(principal.Scope.Type)))
        {
            context.Result = new ApiErrorResult(403, new ResultError(ErrorCodes.Forbidden, "You do not have permission to do this", null, null, null));
        }
        return Task.CompletedTask;
    }
}
