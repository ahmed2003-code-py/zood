using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Platform.Crypto;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.RateLimit;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Identity;

/// <summary>What a signed-in user can do to their own account (Java <c>Account</c>), on the request's transaction.</summary>
public sealed class Account(IdentityStore store, PasswordHasher passwords, IAuditLog audit, IDbSession db, TimeProvider clock,
    IRateLimiter limiter)
{
    public const int PasswordChangeAttempts = 10;

    public async Task<Result<UserView>> MeAsync(ZawedPrincipal principal, CancellationToken ct) =>
        await store.UserAsync(db.Current, principal.UserId, ct) is { } user ? Result<UserView>.Ok(user) : Result<UserView>.NotFound("User");

    /// <summary>
    /// Changes the password and signs the user out everywhere else: whoever knew the old password must not keep a session.
    /// </summary>
    public async Task<Result> ChangePasswordAsync(ZawedPrincipal principal, string currentPassword, string newPassword, CancellationToken ct)
    {
        // A stolen access token must not become a way to brute-force the current password.
        limiter.Acquire("password-change:" + principal.UserId, PasswordChangeAttempts, TimeSpan.FromMinutes(15));
        var tx = db.Current;
        if (await store.UserAsync(tx, principal.UserId, ct) is not { } user || await store.PasswordHashAsync(tx, user.Id, ct) is not { } hash)
        {
            return Result.NotFound("User");
        }
        if (!passwords.Matches(currentPassword, hash))
        {
            return Result.Fail(401, "INVALID_CREDENTIALS", "The current password is not correct", "currentPassword");
        }
        if (SecureTokens.ConstantTimeEquals(currentPassword, newPassword))
        {
            return Result.Fail(400, "PASSWORD_REUSED", "Choose a different password", "newPassword");
        }
        if (PasswordPolicy.Check(newPassword, user.Email) is { } weak)
        {
            return weak;
        }
        var now = clock.GetUtcNow();
        await store.SetPasswordAsync(tx, user.Id, passwords.Hash(newPassword), false, user.Id, now, ct);
        var revoked = await store.RevokeSessionsOfAsync(tx, user.Id, principal.SessionId, "PASSWORD_CHANGED", now, ct);
        await audit.RecordAsync(tx, new AuditActor(user.Id, user.Role), "PASSWORD_CHANGED", "USER", user.Id, user.Scope,
            new Dictionary<string, object?> { ["otherSessionsEnded"] = revoked, ["wasTemporary"] = user.MustChangePassword }, ct);
        return Result.NoContent();
    }

    public async Task<Result<IReadOnlyList<SessionSummaryResponse>>> SessionsAsync(ZawedPrincipal principal, CancellationToken ct) =>
        Result<IReadOnlyList<SessionSummaryResponse>>.Ok((await store.ActiveSessionsAsync(db.Current, principal.UserId, clock.GetUtcNow(), ct))
            .Select(s => new SessionSummaryResponse(s.Id, s.Id == principal.SessionId, s.CreatedAt, s.LastSeenAt, s.UserAgent, s.IpPrefix))
            .ToList());

    public async Task<Result> EndSessionAsync(ZawedPrincipal principal, Guid sessionId, CancellationToken ct)
    {
        var tx = db.Current;
        if (!await store.RevokeOwnSessionAsync(tx, principal.UserId, sessionId, clock.GetUtcNow(), ct))
        {
            return Result.NotFound("Session");
        }
        if (await store.UserAsync(tx, principal.UserId, ct) is not { } user)
        {
            return Result.NotFound("User");
        }
        await audit.RecordAsync(tx, new AuditActor(user.Id, user.Role), "SESSION_REVOKED", "USER", user.Id, user.Scope,
            new Dictionary<string, object?> { ["sessionId"] = sessionId.ToString(), ["reason"] = "SIGNED_OUT_ELSEWHERE" }, ct);
        return Result.NoContent();
    }
}
