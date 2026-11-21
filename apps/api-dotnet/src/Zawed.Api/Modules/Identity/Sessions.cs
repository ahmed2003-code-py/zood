using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Platform.Crypto;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Identity;

/// <summary>A new access token, and a new refresh token unless this was the loser of a refresh race.</summary>
public sealed record IssuedSession(string AccessToken, string? RefreshToken, UserView User);

/// <summary>
/// Sessions and their refresh tokens (contract §3; Java <c>Sessions</c>).
/// <para>The refresh token is 32 random bytes, stored only as its SHA-256. Every refresh replaces it
/// (compare-and-swap, so two racing refreshes cannot both win). The token it replaced stays valid for
/// <see cref="RaceGrace"/> so a request already in flight with it gets an access token instead of a sign-out;
/// presented any later, it is treated as stolen and the whole session is revoked.</para>
/// <para>Like the Java class, the lookups and the rotation run outside any scope through the V3 doors (sessions have
/// no row-level security); what changes a user's record, the reuse revocation and the logout with their audit
/// rows, runs in a transaction scoped to the session's own user (a reviewed escalation).</para>
/// </summary>
public sealed class Sessions(IdentityStore store, AccessTokens tokens, IAuditLog audit, ITransactions transactions, TimeProvider clock,
    ILogger<Sessions> logger) : ISessionVerifier
{
    public static readonly TimeSpan RaceGrace = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PlatformIdle = TimeSpan.FromHours(12);
    public static readonly TimeSpan ScopedIdle = TimeSpan.FromDays(7);
    public static readonly TimeSpan Absolute = TimeSpan.FromDays(30);

    /// <summary>Starts a session for a user whose identity was just proven. Runs in the caller's transaction.</summary>
    public async Task<IssuedSession> StartAsync(DbTx tx, Guid userId, Role role, Scope scope, HttpContext request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var sessionId = Guid.CreateVersion7(now);
        var refreshToken = SecureTokens.Random(32);
        await store.InsertSessionAsync(tx, sessionId, userId, SecureTokens.Sha256Hex(refreshToken), now, now + IdleFor(role), now + Absolute,
            ClientInfo.UserAgent(request), ClientInfo.PrefixOf(request.Connection.RemoteIpAddress), ct);
        await audit.RecordAsync(tx, new AuditActor(userId, role), "LOGIN_SUCCEEDED", "USER", userId, scope,
            new Dictionary<string, object?> { ["sessionId"] = sessionId.ToString() }, ct);
        var user = await store.UserAsync(tx, userId, ct) ?? throw Expired();
        return new IssuedSession(tokens.Issue(userId, sessionId, role, scope), refreshToken, user);
    }

    /// <summary>Exchanges a refresh token for a new access token (and a new refresh token).</summary>
    public async Task<IssuedSession> RefreshAsync(string? refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw Expired();
        }
        var presented = SecureTokens.Sha256Hex(refreshToken);
        var session = await Unscoped(tx => store.SessionByRefreshHashAsync(tx, presented, ct), ct) ?? throw Expired();
        var now = clock.GetUtcNow();
        // A session started at the Super Admin's separate door, removed in B7 W10, is not extended through the one door
        // that replaced it: nothing that door issued outlives it (ADR 0008 amendment).
        if (session.LegacyAdminDoor || !session.UsableAt(now))
        {
            throw Expired();
        }

        if (presented == session.PreviousRefreshTokenHash)
        {
            var racing = session.RotatedAt is { } rotated && now < rotated + RaceGrace;
            if (!racing)
            {
                await RevokeForReuseAsync(session, ct);
                throw new ApiException(401, "REFRESH_REUSE_DETECTED", "A refresh token was used twice; the session has been ended");
            }
            // Lost a race with a concurrent refresh: the winner's cookie is the live one.
            return new IssuedSession(tokens.Issue(session.UserId, session.SessionId, session.Role, session.Scope), null, ViewOf(session));
        }

        var next = SecureTokens.Random(32);
        var idleEnd = Min(now + IdleFor(session.Role), session.AbsoluteExpiresAt);
        var rotatedNow = await Unscoped(tx => store.RotateAsync(tx, session.SessionId, presented, SecureTokens.Sha256Hex(next), now, idleEnd, ct), ct);
        if (!rotatedNow)
        {
            // Someone rotated between our read and our write: same as losing the race.
            return new IssuedSession(tokens.Issue(session.UserId, session.SessionId, session.Role, session.Scope), null, ViewOf(session));
        }
        return new IssuedSession(tokens.Issue(session.UserId, session.SessionId, session.Role, session.Scope), next, ViewOf(session));
    }

    /// <summary>Ends the session a refresh token belongs to. Unknown tokens are ignored: logout always succeeds.</summary>
    public async Task EndAsync(string? refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }
        var hash = SecureTokens.Sha256Hex(refreshToken);
        var session = await Unscoped(tx => store.SessionByRefreshHashAsync(tx, hash, ct), ct);
        if (session is null)
        {
            return;
        }
        await AsUserAsync(session, async tx =>
        {
            if (await store.RevokeSessionAsync(tx, session.SessionId, "LOGOUT", clock.GetUtcNow(), ct))
            {
                await audit.RecordAsync(tx, new AuditActor(session.UserId, session.Role), "LOGOUT", "USER", session.UserId, session.Scope,
                    new Dictionary<string, object?> { ["sessionId"] = session.SessionId.ToString() }, ct);
            }
        }, ct);
    }

    public async Task<ZawedPrincipal?> VerifyAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var session = await Unscoped(tx => store.SessionByIdAsync(tx, sessionId, cancellationToken), cancellationToken);
        return session is not null && session.UserId == userId && session.UsableAt(now)
            ? new ZawedPrincipal(session.UserId, session.SessionId, session.Role, session.Scope, session.MustChangePassword)
            : null;
    }

    private async Task RevokeForReuseAsync(SessionRow session, CancellationToken ct)
    {
        logger.LogWarning("Refresh token reuse detected; revoking session {Session} of user {User}", session.SessionId, session.UserId);
        await AsUserAsync(session, async tx =>
        {
            await store.RevokeSessionAsync(tx, session.SessionId, "REFRESH_REUSE_DETECTED", clock.GetUtcNow(), ct);
            await audit.RecordAsync(tx, AuditActor.Anonymous, "SESSION_REVOKED", "USER", session.UserId, session.Scope,
                new Dictionary<string, object?> { ["reason"] = "REFRESH_REUSE_DETECTED", ["sessionId"] = session.SessionId.ToString() }, ct);
        }, ct);
    }

    /// <summary>A transaction without a scope, for the V3 doors and the session table (Java ran these outside one).</summary>
    private Task<T> Unscoped<T>(Func<DbTx, Task<T>> work, CancellationToken ct) => transactions.RunAsync(DbScopeContext.None, work, ct: ct);

    /// <summary>
    /// A transaction scoped to the session's own user. Escalation, reviewed: the caller proved it holds the session's
    /// refresh token (its hash matched a stored one), and the scope is the one the database holds for that user.
    /// </summary>
    private Task AsUserAsync(SessionRow session, Func<DbTx, Task> work, CancellationToken ct) =>
        transactions.RunAsync(DbScopeContext.Escalate(session.Scope, session.UserId), async tx =>
        {
            await work(tx);
            return true;
        }, ct: ct);

    private static UserView ViewOf(SessionRow session) => new(session.UserId, session.Email, session.FullName, session.Role, session.Scope,
        session.Status, session.MustChangePassword, session.MfaEnabled, null, session.CreatedAt);

    public static TimeSpan IdleFor(Role role) => role.IsPlatform() ? PlatformIdle : ScopedIdle;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    public static ApiException Expired() => new(401, "SESSION_EXPIRED", "Sign in again");
}
