using Zawed.Api.Platform.Security;

namespace Zawed.Api.Modules.Identity;

public enum UserStatus
{
    ACTIVE,
    SUSPENDED,
    DEACTIVATED,
}

/// <summary>What the sign-in path needs (<c>auth_login_lookup</c>).</summary>
public sealed record LoginCandidate(Guid Id, string Email, string FullName, string PasswordHash, Role Role, Scope Scope,
    UserStatus Status, bool MustChangePassword, DateTimeOffset? LockedUntil, bool MfaEnabled);

/// <summary>A user as the admin screens and the profile see it. Never carries secrets.</summary>
public sealed record UserView(Guid Id, string Email, string FullName, Role Role, Scope Scope, UserStatus Status,
    bool MustChangePassword, bool MfaEnabled, DateTimeOffset? LockedUntil, DateTimeOffset CreatedAt)
{
    public const string Columns = "id, email, full_name, role, scope_type, scope_id, status, must_change_password, "
        + "mfa_enabled, locked_until, created_at";
}

/// <summary>
/// A session joined with its user, as the <c>auth_session_*</c> functions return it.
/// <paramref name="LegacyAdminDoor"/>: started at the Super Admin's separate door, removed in B7 W10 (ADR 0008
/// amendment); such a session is never refreshed. Every session started since records door USER.
/// </summary>
public sealed record SessionRow(Guid SessionId, Guid UserId, bool LegacyAdminDoor, string RefreshTokenHash,
    string? PreviousRefreshTokenHash, DateTimeOffset? RotatedAt, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    DateTimeOffset AbsoluteExpiresAt, DateTimeOffset? RevokedAt, string Email, string FullName, Role Role, Scope Scope,
    UserStatus Status, bool MustChangePassword, bool MfaEnabled)
{
    public bool UsableAt(DateTimeOffset now) =>
        RevokedAt is null && Status == UserStatus.ACTIVE && now < ExpiresAt && now < AbsoluteExpiresAt;
}

/// <summary>The second sign-in step.</summary>
public sealed record Challenge(Guid Id, Guid UserId, string Purpose, Scope Scope, byte[]? PendingSecretEncrypted, int Attempts,
    DateTimeOffset ExpiresAt, DateTimeOffset? UsedAt);

public sealed record SessionSummary(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, string? UserAgent, string? IpPrefix);

public sealed record MfaState(byte[] SecretEncrypted, long? LastUsedStep);
