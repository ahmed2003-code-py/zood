using System.Security.Claims;

namespace Zawed.Api.Platform.Security;

/// <summary>
/// The signed-in user of the current request, built from the access token and the session row, which is
/// read on every request: role and scope are what the database says now, not what the token said when
/// it was issued (ADR 0008). Carried as claims on <see cref="HttpContext.User"/>.
/// </summary>
public sealed record ZawedPrincipal(Guid UserId, Guid SessionId, Role Role, Scope Scope, bool MustChangePassword)
{
    public const string AuthenticationType = "zawed-user";
    private const string UserIdClaim = "zawed:user";
    private const string SessionClaim = "zawed:session";
    private const string RoleClaim = "zawed:role";
    private const string ScopeTypeClaim = "zawed:scope-type";
    private const string ScopeIdClaim = "zawed:scope-id";
    private const string MustChangeClaim = "zawed:must-change-password";

    public bool Can(Permission permission) => Permissions.Of(Role).Contains(permission);

    public ClaimsPrincipal ToClaimsPrincipal()
    {
        var claims = new List<Claim>
        {
            new(UserIdClaim, UserId.ToString()),
            new(SessionClaim, SessionId.ToString()),
            new(RoleClaim, Role.ToString()),
            new(ScopeTypeClaim, Scope.Type.ToString()),
            new(MustChangeClaim, MustChangePassword ? "true" : "false"),
            new(ClaimTypes.NameIdentifier, UserId.ToString()),
        };
        if (Scope.Id is { } id)
        {
            claims.Add(new(ScopeIdClaim, id.ToString()));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationType, ClaimTypes.NameIdentifier, RoleClaim));
    }

    /// <summary>The principal of a request, or null when the caller is not a signed-in user.</summary>
    public static ZawedPrincipal? From(ClaimsPrincipal? user)
    {
        var identity = user?.Identities.FirstOrDefault(i => i.AuthenticationType == AuthenticationType && i.IsAuthenticated);
        if (identity is null)
        {
            return null;
        }
        var scopeType = Enum.Parse<ScopeType>(identity.FindFirst(ScopeTypeClaim)!.Value);
        var scopeId = identity.FindFirst(ScopeIdClaim) is { } s ? Guid.Parse(s.Value) : (Guid?)null;
        return new ZawedPrincipal(
            Guid.Parse(identity.FindFirst(UserIdClaim)!.Value),
            Guid.Parse(identity.FindFirst(SessionClaim)!.Value),
            Enum.Parse<Role>(identity.FindFirst(RoleClaim)!.Value),
            new Scope(scopeType, scopeId),
            identity.FindFirst(MustChangeClaim)?.Value == "true");
    }
}

/// <summary>
/// Checks, on every request, that the session an access token names is still usable, and returns the
/// principal as the database knows it now. Implemented by the identity module; the platform only calls it.
/// </summary>
public interface ISessionVerifier
{
    /// <summary>Null when the session is unknown, revoked or expired, or its user is no longer active.</summary>
    Task<ZawedPrincipal?> VerifyAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken);
}
