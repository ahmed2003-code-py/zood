using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;

namespace Zawed.Api.Modules.Identity;

/// <summary>
/// Every query of the identity module (Java <c>IdentityStore</c>), with the Java SQL as it is. Reads and writes of
/// <c>users</c> run inside a transaction whose scope row-level security checks; the sign-in path, which has no
/// scope yet, goes through the narrow <c>auth_*</c> SECURITY DEFINER functions from V3 instead. Each method takes
/// the transaction it runs in: the sign-in path opens several of its own.
/// </summary>
public sealed class IdentityStore
{
    /// <summary>
    /// <c>door</c> is history since B7 W10 (ADR 0008 amendment): every role signs in at one path, so a new session
    /// or challenge always records USER. Rows written before it may say ADMIN.
    /// </summary>
    private const string Door = "USER";

    // ------------------------------------------------------------------------------------- rows

    private sealed class UserRow
    {
        public Guid Id { get; set; }
        public string Email { get; set; } = "";
        public string Full_Name { get; set; } = "";
        public string? Password_Hash { get; set; }
        public string Role { get; set; } = "";
        public string Scope_Type { get; set; } = "";
        public Guid? Scope_Id { get; set; }
        public string Status { get; set; } = "";
        public bool Must_Change_Password { get; set; }
        public DateTime? Locked_Until { get; set; }
        public bool Mfa_Enabled { get; set; }
        public DateTime Created_At { get; set; }

        public LoginCandidate ToCandidate() => new(Id, Email, Full_Name, Password_Hash!, Enum.Parse<Role>(Role),
            ScopeOf(Scope_Type, Scope_Id), Enum.Parse<UserStatus>(Status), Must_Change_Password, Utc(Locked_Until), Mfa_Enabled);

        public UserView ToView() => new(Id, Email, Full_Name, Enum.Parse<Role>(Role), ScopeOf(Scope_Type, Scope_Id),
            Enum.Parse<UserStatus>(Status), Must_Change_Password, Mfa_Enabled, Utc(Locked_Until), Utc(Created_At));
    }

    private sealed class SessionRowData
    {
        public Guid Session_Id { get; set; }
        public Guid User_Id { get; set; }
        public string Door { get; set; } = "";
        public string Refresh_Token_Hash { get; set; } = "";
        public string? Previous_Refresh_Token_Hash { get; set; }
        public DateTime? Rotated_At { get; set; }
        public DateTime Created_At { get; set; }
        public DateTime Expires_At { get; set; }
        public DateTime Absolute_Expires_At { get; set; }
        public DateTime? Revoked_At { get; set; }
        public string Email { get; set; } = "";
        public string Full_Name { get; set; } = "";
        public string Role { get; set; } = "";
        public string Scope_Type { get; set; } = "";
        public Guid? Scope_Id { get; set; }
        public string Status { get; set; } = "";
        public bool Must_Change_Password { get; set; }
        public bool Mfa_Enabled { get; set; }

        public SessionRow ToRow() => new(Session_Id, User_Id, Door == "ADMIN", Refresh_Token_Hash, Previous_Refresh_Token_Hash,
            Utc(Rotated_At), Utc(Created_At), Utc(Expires_At), Utc(Absolute_Expires_At), Utc(Revoked_At), Email, Full_Name,
            Enum.Parse<Role>(Role), ScopeOf(Scope_Type, Scope_Id), Enum.Parse<UserStatus>(Status), Must_Change_Password, Mfa_Enabled);
    }

    private sealed class ChallengeRow
    {
        public Guid Id { get; set; }
        public Guid User_Id { get; set; }
        public string Purpose { get; set; } = "";
        public string Scope_Type { get; set; } = "";
        public Guid? Scope_Id { get; set; }
        public byte[]? Pending_Secret_Encrypted { get; set; }
        public int Attempts { get; set; }
        public DateTime Expires_At { get; set; }
        public DateTime? Used_At { get; set; }
    }

    private sealed class SessionSummaryRow
    {
        public Guid Id { get; set; }
        public DateTime Created_At { get; set; }
        public DateTime Last_Seen_At { get; set; }
        public string? User_Agent { get; set; }
        public string? Ip_Prefix { get; set; }
    }

    private sealed class MfaRow
    {
        public byte[] Mfa_Secret_Encrypted { get; set; } = [];
        public long? Mfa_Last_Used_Step { get; set; }
    }

    // ---------------------------------------------------------------------------- sign-in path

    public async Task<LoginCandidate?> LoginLookupAsync(DbTx tx, string email, CancellationToken ct) =>
        (await tx.QuerySingleOrDefaultAsync<UserRow>("select * from auth_login_lookup(@email)", new { email }, ct))?.ToCandidate();

    /// <summary>Counts a failure (and maybe locks), audited by the function itself. Returns the lock end, if locked.</summary>
    public async Task<DateTimeOffset?> RecordLoginFailureAsync(DbTx tx, Guid userId, bool count, string reason, int maxAttempts,
        int lockSeconds, DateTimeOffset now, Guid auditId, Guid lockAuditId, string? requestId, string? ipPrefix, CancellationToken ct) =>
        Utc(await tx.QuerySingleOrDefaultAsync<DateTime?>("""
            select auth_record_login_failure(@user, @count, @reason, @max, @lock, @now, @audit, @lockAudit,
                                             @requestId, @ip) as locked_until
            """, new
        {
            user = userId, count, reason, max = maxAttempts, @lock = lockSeconds, now = now.UtcDateTime, audit = auditId,
            lockAudit = lockAuditId, requestId, ip = ipPrefix,
        }, ct));

    public Task<bool> SuperAdminExistsAsync(DbTx tx, CancellationToken ct) =>
        tx.QuerySingleAsync<bool>("select auth_super_admin_exists()", ct: ct);

    public Task<bool> BootstrapSuperAdminAsync(DbTx tx, Guid id, string email, string fullName, string passwordHash, DateTimeOffset now,
        Guid auditId, string? requestId, string? ipPrefix, CancellationToken ct) =>
        tx.QuerySingleAsync<bool>("select auth_bootstrap_super_admin(@id, @email, @name, @hash, @now, @audit, @requestId, @ip)", new
        {
            id, email, name = fullName, hash = passwordHash, now = now.UtcDateTime, audit = auditId, requestId, ip = ipPrefix,
        }, ct);

    // -------------------------------------------------------------------------------- sessions

    public async Task<SessionRow?> SessionByIdAsync(DbTx tx, Guid sessionId, CancellationToken ct) =>
        (await tx.QuerySingleOrDefaultAsync<SessionRowData>("select * from auth_session_by_id(@id)", new { id = sessionId }, ct))?.ToRow();

    public async Task<SessionRow?> SessionByRefreshHashAsync(DbTx tx, string hash, CancellationToken ct) =>
        (await tx.QuerySingleOrDefaultAsync<SessionRowData>("select * from auth_session_by_refresh_hash(@hash)", new { hash }, ct))?.ToRow();

    public Task<int> InsertSessionAsync(DbTx tx, Guid id, Guid userId, string refreshHash, DateTimeOffset now, DateTimeOffset expiresAt,
        DateTimeOffset absoluteExpiresAt, string? userAgent, string? ipPrefix, CancellationToken ct) =>
        tx.ExecuteAsync("""
            insert into sessions (id, user_id, door, refresh_token_hash, created_at, last_seen_at, expires_at,
                                  absolute_expires_at, user_agent, ip_prefix)
            values (@id, @user, @door, @hash, @now, @now, @expires, @absolute, @agent, @ip)
            """, new
        {
            id, user = userId, door = Door, hash = refreshHash, now = now.UtcDateTime, expires = expiresAt.UtcDateTime,
            absolute = absoluteExpiresAt.UtcDateTime, agent = userAgent, ip = ipPrefix,
        }, ct);

    /// <summary>
    /// Compare-and-swap rotation: succeeds only if nobody rotated this session since <paramref name="currentHash"/> was
    /// read. Two refreshes racing with the same token cannot both win.
    /// </summary>
    public async Task<bool> RotateAsync(DbTx tx, Guid sessionId, string currentHash, string newHash, DateTimeOffset now,
        DateTimeOffset expiresAt, CancellationToken ct) =>
        await tx.ExecuteAsync("""
            update sessions
            set previous_refresh_token_hash = refresh_token_hash, refresh_token_hash = @new, rotated_at = @now,
                last_seen_at = @now, expires_at = @expires
            where id = @id and refresh_token_hash = @current and revoked_at is null
            """, new { id = sessionId, current = currentHash, @new = newHash, now = now.UtcDateTime, expires = expiresAt.UtcDateTime }, ct) == 1;

    public async Task<bool> RevokeSessionAsync(DbTx tx, Guid sessionId, string reason, DateTimeOffset now, CancellationToken ct) =>
        await tx.ExecuteAsync("update sessions set revoked_at = @now, revoked_reason = @reason where id = @id and revoked_at is null",
            new { id = sessionId, reason, now = now.UtcDateTime }, ct) == 1;

    public Task<int> RevokeSessionsOfAsync(DbTx tx, Guid userId, Guid? except, string reason, DateTimeOffset now, CancellationToken ct) =>
        tx.ExecuteAsync("""
            update sessions set revoked_at = @now, revoked_reason = @reason
            where user_id = @user and revoked_at is null and (cast(@except as uuid) is null or id <> @except)
            """, new { user = userId, except, reason, now = now.UtcDateTime }, ct);

    public async Task<IReadOnlyList<SessionSummary>> ActiveSessionsAsync(DbTx tx, Guid userId, DateTimeOffset now, CancellationToken ct) =>
        (await tx.QueryAsync<SessionSummaryRow>("""
            select id, created_at, last_seen_at, user_agent, ip_prefix from sessions
            where user_id = @user and revoked_at is null and expires_at > @now and absolute_expires_at > @now
            order by id desc limit 50
            """, new { user = userId, now = now.UtcDateTime }, ct))
        .Select(r => new SessionSummary(r.Id, Utc(r.Created_At), Utc(r.Last_Seen_At), r.User_Agent, r.Ip_Prefix)).ToList();

    public async Task<bool> RevokeOwnSessionAsync(DbTx tx, Guid userId, Guid sessionId, DateTimeOffset now, CancellationToken ct) =>
        await tx.ExecuteAsync("""
            update sessions set revoked_at = @now, revoked_reason = 'SIGNED_OUT_ELSEWHERE'
            where id = @id and user_id = @user and revoked_at is null
            """, new { id = sessionId, user = userId, now = now.UtcDateTime }, ct) == 1;

    // ------------------------------------------------------------------------------ challenges

    public Task<int> InsertChallengeAsync(DbTx tx, Guid id, Guid userId, string purpose, Scope scope, string tokenHash, DateTimeOffset now,
        DateTimeOffset expiresAt, string? userAgent, string? ipPrefix, CancellationToken ct) =>
        tx.ExecuteAsync("""
            insert into mfa_challenges (id, user_id, purpose, door, token_hash, scope_type, scope_id, created_at,
                                        expires_at, user_agent, ip_prefix)
            values (@id, @user, @purpose, @door, @hash, @scopeType, @scopeId, @now, @expires, @agent, @ip)
            """, new
        {
            id, user = userId, purpose, door = Door, hash = tokenHash, scopeType = scope.Type.ToString(), scopeId = scope.Id,
            now = now.UtcDateTime, expires = expiresAt.UtcDateTime, agent = userAgent, ip = ipPrefix,
        }, ct);

    public async Task<Challenge?> ChallengeByTokenHashAsync(DbTx tx, string hash, CancellationToken ct)
    {
        var row = await tx.QuerySingleOrDefaultAsync<ChallengeRow>("select * from mfa_challenges where token_hash = @hash", new { hash }, ct);
        return row is null ? null : new Challenge(row.Id, row.User_Id, row.Purpose, ScopeOf(row.Scope_Type, row.Scope_Id),
            row.Pending_Secret_Encrypted, row.Attempts, Utc(row.Expires_At), Utc(row.Used_At));
    }

    /// <summary>Counts a wrong code; returns the new count.</summary>
    public Task<int> CountChallengeAttemptAsync(DbTx tx, Guid id, CancellationToken ct) =>
        tx.QuerySingleAsync<int>("update mfa_challenges set attempts = attempts + 1 where id = @id returning attempts", new { id }, ct);

    /// <summary>Single use: only the first caller marks it used.</summary>
    public async Task<bool> UseChallengeAsync(DbTx tx, Guid id, DateTimeOffset now, CancellationToken ct) =>
        await tx.ExecuteAsync("update mfa_challenges set used_at = @now where id = @id and used_at is null",
            new { id, now = now.UtcDateTime }, ct) == 1;

    public Task<int> SetPendingSecretAsync(DbTx tx, Guid id, byte[] encrypted, CancellationToken ct) =>
        tx.ExecuteAsync("update mfa_challenges set pending_secret_encrypted = @secret where id = @id", new { id, secret = encrypted }, ct);

    // ----------------------------------------------------------------------------------- users

    public async Task<UserView?> UserAsync(DbTx tx, Guid id, CancellationToken ct) =>
        (await tx.QuerySingleOrDefaultAsync<UserRow>($"select {UserView.Columns} from users where id = @id", new { id }, ct))?.ToView();

    public Task<string?> PasswordHashAsync(DbTx tx, Guid id, CancellationToken ct) =>
        tx.QuerySingleOrDefaultAsync<string>("select password_hash from users where id = @id", new { id }, ct);

    public async Task<MfaState?> MfaStateAsync(DbTx tx, Guid id, CancellationToken ct)
    {
        var row = await tx.QuerySingleOrDefaultAsync<MfaRow>(
            "select mfa_secret_encrypted, mfa_last_used_step from users where id = @id and mfa_enabled", new { id }, ct);
        return row is null ? null : new MfaState(row.Mfa_Secret_Encrypted, row.Mfa_Last_Used_Step);
    }

    /// <summary>Accepts a TOTP step only if it is later than every step accepted before (no replay).</summary>
    public async Task<bool> AcceptStepAsync(DbTx tx, Guid userId, long step, DateTimeOffset now, CancellationToken ct) =>
        await tx.ExecuteAsync("""
            update users set mfa_last_used_step = @step, updated_at = @now
            where id = @id and (mfa_last_used_step is null or mfa_last_used_step < @step)
            """, new { id = userId, step, now = now.UtcDateTime }, ct) == 1;

    /// <summary>
    /// Enables MFA only if it is not enabled yet: an older enrolment challenge must never replace the authenticator
    /// the user has since set up. False when it was already enabled.
    /// </summary>
    public async Task<bool> EnableMfaAsync(DbTx tx, Guid userId, byte[] secretEncrypted, long step, DateTimeOffset now, CancellationToken ct) =>
        await tx.ExecuteAsync("""
            update users set mfa_secret_encrypted = @secret, mfa_enabled = true, mfa_last_used_step = @step,
                             updated_at = @now, updated_by = @id
            where id = @id and not mfa_enabled
            """, new { id = userId, secret = secretEncrypted, step, now = now.UtcDateTime }, ct) == 1;

    /// <summary>Voids every open sign-in challenge of a user, after an administrator changes the account.</summary>
    public Task<int> VoidChallengesOfAsync(DbTx tx, Guid userId, DateTimeOffset now, CancellationToken ct) =>
        tx.ExecuteAsync("update mfa_challenges set used_at = @now where user_id = @user and used_at is null",
            new { user = userId, now = now.UtcDateTime }, ct);

    public Task<int> ClearLoginFailuresAsync(DbTx tx, Guid userId, DateTimeOffset now, CancellationToken ct) =>
        tx.ExecuteAsync("""
            update users set failed_login_count = 0, locked_until = null, updated_at = @now
            where id = @id and (failed_login_count <> 0 or locked_until is not null)
            """, new { id = userId, now = now.UtcDateTime }, ct);

    public Task<int> SetPasswordAsync(DbTx tx, Guid userId, string hash, bool mustChange, Guid actor, DateTimeOffset now, CancellationToken ct) =>
        tx.ExecuteAsync("""
            update users set password_hash = @hash, must_change_password = @mustChange, password_changed_at = @now,
                             failed_login_count = 0, locked_until = null, updated_at = @now, updated_by = @actor
            where id = @id
            """, new { id = userId, hash, mustChange, actor, now = now.UtcDateTime }, ct);

    public Task<int> InsertUserAsync(DbTx tx, Guid id, string email, string fullName, string passwordHash, Role role, Scope scope, Guid actor,
        DateTimeOffset now, CancellationToken ct) =>
        tx.ExecuteAsync("""
            insert into users (id, email, full_name, password_hash, role, scope_type, scope_id, status,
                               must_change_password, password_changed_at, created_at, created_by, updated_at, updated_by)
            values (@id, @email, @name, @hash, @role, @scopeType, @scopeId, 'ACTIVE', true, @now, @now, @actor, @now,
                    @actor)
            """, new
        {
            id, email, name = fullName, hash = passwordHash, role = role.ToString(), scopeType = scope.Type.ToString(),
            scopeId = scope.Id, actor, now = now.UtcDateTime,
        }, ct);

    public Task<bool> EmailTakenAsync(DbTx tx, string email, CancellationToken ct) =>
        tx.QuerySingleAsync<bool>("select exists (select 1 from auth_login_lookup(@email))", new { email }, ct);

    public Task<int> SetStatusAsync(DbTx tx, Guid userId, UserStatus status, Guid actor, DateTimeOffset now, CancellationToken ct) =>
        tx.ExecuteAsync("update users set status = @status, updated_at = @now, updated_by = @actor where id = @id",
            new { id = userId, status = status.ToString(), actor, now = now.UtcDateTime }, ct);

    public Task<int> UnlockAsync(DbTx tx, Guid userId, Guid actor, DateTimeOffset now, CancellationToken ct) =>
        tx.ExecuteAsync("""
            update users set failed_login_count = 0, locked_until = null, updated_at = @now, updated_by = @actor
            where id = @id
            """, new { id = userId, actor, now = now.UtcDateTime }, ct);

    public Task<int> ResetMfaAsync(DbTx tx, Guid userId, Guid actor, DateTimeOffset now, CancellationToken ct) =>
        tx.ExecuteAsync("""
            update users set mfa_enabled = false, mfa_secret_encrypted = null, mfa_last_used_step = null,
                             updated_at = @now, updated_by = @actor
            where id = @id
            """, new { id = userId, actor, now = now.UtcDateTime }, ct);

    /// <summary>Locks the active Super Admin rows so two demotions cannot both pass the "last one" check.</summary>
    public async Task<int> LockActiveSuperAdminsAsync(DbTx tx, CancellationToken ct) =>
        (await tx.QueryAsync<Guid>("select id from users where role = 'SUPER_ADMIN' and status = 'ACTIVE' for update", ct: ct)).Count();

    public async Task<IReadOnlyList<UserView>> ListUsersAsync(DbTx tx, IReadOnlyList<Role> roles, UserStatus? status, string? query,
        Guid? cursor, int limit, CancellationToken ct) =>
        (await tx.QueryAsync<UserRow>($"select {UserView.Columns}" + """
             from users
            where role = any(@roles)
              and (cast(@status as text) is null or status = @status)
              and (cast(@q as text) is null or lower(email) like @q or lower(full_name) like @q)
              and (cast(@cursor as uuid) is null or id < @cursor)
            order by id desc
            limit @limit
            """, new
        {
            roles = roles.Select(r => r.ToString()).ToArray(),
            status = status?.ToString(),
            q = string.IsNullOrWhiteSpace(query) ? null : "%" + EscapeLike(query.Trim().ToLowerInvariant()) + "%",
            cursor,
            limit,
        }, ct)).Select(r => r.ToView()).ToList();

    /// <summary>Whether the company a new user is scoped to exists (Java <c>CompanyDirectory.find</c>, whatever its status),
    /// read in the caller's transaction. An operator scope is asked of the network module's <c>IOperatorDirectory</c>.</summary>
    public Task<bool> ScopeExistsAsync(DbTx tx, Scope scope, CancellationToken ct) => scope.Type switch
    {
        ScopeType.PLATFORM => Task.FromResult(true),
        ScopeType.COMPANY => tx.QuerySingleAsync<bool>("select exists (select 1 from companies where id = @id)", new { id = scope.Id }, ct),
        _ => throw new InvalidOperationException("An operator scope is checked through IOperatorDirectory"),
    };

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public static Scope ScopeOf(string type, Guid? id) => new(Enum.Parse<ScopeType>(type), id);

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset? Utc(DateTime? value) => value is { } v ? Utc(v) : null;
}
