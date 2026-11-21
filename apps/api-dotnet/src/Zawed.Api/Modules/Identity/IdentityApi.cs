using System.Text.Json.Serialization;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Identity;

// Request and response bodies of the identity endpoints (docs/architecture/b1-identity-contract.md; Java IdentityApi).
// Sizes are bounded so no field can carry an unbounded payload into Argon2 or the database.

// ------------------------------------------------------------------------------------------ requests

public sealed record LoginRequest([NotBlank, Size(max: 254)] string Email, [NotBlank, Size(max: 256)] string Password);

public sealed record ChallengeRequest([NotBlank, Size(max: 128)] string ChallengeToken);

public sealed record ChallengeCodeRequest([NotBlank, Size(max: 128)] string ChallengeToken, [NotBlank, Size(min: 6, max: 6)] string Code);

public sealed record ChangePasswordRequest([NotBlank, Size(max: 256)] string CurrentPassword, [NotBlank, Size(max: 256)] string NewPassword);

public sealed record SetupRequest([NotBlank, Size(max: 256)] string SetupToken, [NotBlank, Size(max: 254)] string Email,
    [NotBlank, Size(max: 200)] string FullName, [NotBlank, Size(max: 256)] string Password);

public sealed record ScopeBody([NotNull] ScopeType? Type, Guid? Id);

public sealed record CreateUserRequest([NotBlank, Size(max: 254)] string Email, [NotBlank, Size(max: 200)] string FullName,
    [NotNull] Role? Role, [NotNull] ScopeBody Scope);

public sealed record StatusRequest([NotNull] UserStatus? Status, [NotBlank, Size(max: 500)] string Reason);

public sealed record ReasonRequest([NotBlank, Size(max: 500)] string Reason);

// ----------------------------------------------------------------------------------------- responses

public sealed record ScopeResponse(ScopeType Type, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? Id)
{
    public static ScopeResponse Of(Scope scope) => new(scope.Type, scope.Id);
}

public sealed record UserResponse(Guid Id, string Email, string FullName, Role Role, ScopeResponse Scope, bool MustChangePassword,
    bool MfaEnabled)
{
    public static UserResponse Of(UserView user) =>
        new(user.Id, user.Email, user.FullName, user.Role, ScopeResponse.Of(user.Scope), user.MustChangePassword, user.MfaEnabled);
}

/// <summary>The admin list adds status and lock state.</summary>
public sealed record AdminUserResponse(Guid Id, string Email, string FullName, Role Role, ScopeResponse Scope, UserStatus Status,
    bool MustChangePassword, bool MfaEnabled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? LockedUntil, DateTimeOffset CreatedAt)
{
    public static AdminUserResponse Of(UserView user) => new(user.Id, user.Email, user.FullName, user.Role, ScopeResponse.Of(user.Scope),
        user.Status, user.MustChangePassword, user.MfaEnabled, user.LockedUntil, user.CreatedAt);
}

public sealed record SessionResponse(string AccessToken, long ExpiresInSeconds, UserResponse User);

public enum LoginStatus
{
    AUTHENTICATED,
    MFA_REQUIRED,
    MFA_ENROLLMENT_REQUIRED,
}

/// <summary>A session, or a challenge for the second sign-in step</summary>
public sealed record LoginResponse(
    LoginStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SessionResponse? Session,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ChallengeToken,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ExpiresInSeconds);

public sealed record EnrollmentResponse(string Secret, string OtpauthUri);

/// <param name="TemporaryPassword">Shown once; never retrievable again</param>
public sealed record CreatedUserResponse(AdminUserResponse User, string TemporaryPassword);

/// <param name="TemporaryPassword">Shown once; never retrievable again</param>
public sealed record TemporaryPasswordResponse(string TemporaryPassword);

public sealed record SessionSummaryResponse(Guid Id, bool Current, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, string? UserAgent,
    string? IpPrefix);
