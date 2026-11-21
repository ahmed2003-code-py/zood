using Microsoft.Extensions.Options;
using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Platform.Config;
using Zawed.Api.Platform.Crypto;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.RateLimit;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Identity;

/// <summary>What sign-in returns: a session, or a challenge for the second step.</summary>
public abstract record SignInOutcome
{
    public sealed record Authenticated(IssuedSession Session) : SignInOutcome;

    public sealed record MfaRequired(string ChallengeToken, long ExpiresInSeconds) : SignInOutcome;

    public sealed record EnrollmentRequired(string ChallengeToken, long ExpiresInSeconds) : SignInOutcome;
}

public sealed record Enrollment(string Secret, string OtpauthUri);

/// <summary>
/// The sign-in path (Java <c>SignIn</c>): password, then (for the roles that need one, Super Admin included) a second
/// factor, then a session.
/// <list type="bullet">
/// <item>One path for every role since B7 W10 (owner, 2026-11-20; ADR 0008 amendment). The role comes from the
/// user's row, never from the request, and decides which portal the web app opens.</item>
/// <item>Every refusal before the password is proven looks the same and takes the same time: an unknown email and a
/// wrong password both answer <c>INVALID_CREDENTIALS</c> after a full Argon2 check, whatever the account's role.
/// Only after a correct password does the caller learn that an account is disabled.</item>
/// <item>Five wrong passwords lock the account for 15 minutes; Ops can unlock it. The counter and the failure audits
/// commit in their own transaction (the V3 door), so they outlive the 401.</item>
/// <item>A second-factor code is accepted once: its time step must be later than any accepted before.</item>
/// </list>
/// The path has no principal, so it opens its own transactions, as the Java class did with its
/// <c>TransactionTemplate</c>: unscoped ones for the V3 doors and the challenge table, and, once the password or the
/// challenge has proven who the caller is, ones scoped to that user (a reviewed escalation). A refusal inside a
/// scoped step throws, which rolls that step back. Refusals are <see cref="ApiException"/>s, as in Java: this is the
/// engine below the controller.
/// </summary>
public sealed class SignIn(IdentityStore store, PasswordHasher passwords, Sessions sessions, DataEncryptor encryptor, IAuditLog audit,
    ITransactions transactions, TimeProvider clock, IRateLimiter limiter, IOptions<ZawedOptions> options)
{
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan Lock = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan VerifyChallengeLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan EnrollChallengeLifetime = TimeSpan.FromMinutes(10);
    public const int MaxChallengeAttempts = 5;
    public const int EmailAttempts = 10;
    public static readonly TimeSpan EmailWindow = TimeSpan.FromMinutes(15);
    public const string TotpIssuer = "ZAWED";

    /// <summary>
    /// The second factor at sign-in (ADR 0009). Switched off on zawed-dev only while the owner tests (2026-11-19);
    /// <c>DeploymentGuard</c> refuses to start production with it off. Off means no MFA prompt at all, enrolled or not.
    /// </summary>
    private readonly bool mfaRequired = options.Value.Security.MfaRequired;

    public async Task<SignInOutcome> LoginAsync(string? rawEmail, string password, HttpContext request, CancellationToken ct)
    {
        var email = NormalizeEmail(rawEmail);
        limiter.Acquire("signin-email:" + SecureTokens.Sha256Hex(email), EmailAttempts, EmailWindow);

        var user = await Unscoped(tx => store.LoginLookupAsync(tx, email, ct), ct);
        var now = clock.GetUtcNow();
        if (user is null)
        {
            passwords.Burn(password);
            throw InvalidCredentials();
        }
        if (user.LockedUntil is { } lockedUntil && lockedUntil > now)
        {
            passwords.Burn(password);
            throw Locked(lockedUntil, now);
        }
        if (!passwords.Matches(password, user.PasswordHash))
        {
            var lockEnd = await RecordFailureAsync(user, true, "WRONG_PASSWORD", request, ct);
            if (lockEnd is { } end && end > now)
            {
                throw Locked(end, now);
            }
            throw InvalidCredentials();
        }
        // The password is right. From here on the caller may learn facts about the account.
        if (user.Status != UserStatus.ACTIVE)
        {
            await RecordFailureAsync(user, false, "ACCOUNT_" + user.Status, request, ct);
            throw Disabled();
        }

        return await AsUserAsync(user.Scope, user.Id, async tx =>
        {
            await store.ClearLoginFailuresAsync(tx, user.Id, now, ct);
            if (mfaRequired && (user.MfaEnabled || user.Role.RequiresMfa()))
            {
                return await ChallengeAsync(tx, user.Id, user.Scope, user.MfaEnabled ? "VERIFY" : "ENROLL", request, ct);
            }
            return new SignInOutcome.Authenticated(await sessions.StartAsync(tx, user.Id, user.Role, user.Scope, request, ct));
        }, ct);
    }

    public async Task<SignInOutcome> VerifyMfaAsync(string? challengeToken, string? code, HttpContext request, CancellationToken ct)
    {
        var challenge = await LiveChallengeAsync(challengeToken, "VERIFY", ct);
        var secret = await AsUserAsync(challenge.Scope, challenge.UserId, async tx =>
        {
            var state = await store.MfaStateAsync(tx, challenge.UserId, ct) ?? throw ChallengeExpired();
            return encryptor.Decrypt(state.SecretEncrypted, UserBinding(challenge.UserId));
        }, ct);
        var step = Totp.MatchingStep(secret, code, clock.GetUtcNow().ToUnixTimeSeconds());
        if (step is null)
        {
            throw await WrongCodeAsync(challenge, ct);
        }
        return await AsUserAsync(challenge.Scope, challenge.UserId, async tx =>
        {
            var now = clock.GetUtcNow();
            if (!await store.AcceptStepAsync(tx, challenge.UserId, step.Value, now, ct))
            {
                // The code was right but already used: a replay, or a second tab racing the first.
                throw new ApiException(401, "MFA_INVALID_CODE", "This code was already used");
            }
            return await CompleteAsync(tx, challenge, now, request, ct);
        }, ct);
    }

    public async Task<Enrollment> StartEnrollmentAsync(string? challengeToken, CancellationToken ct)
    {
        var challenge = await LiveChallengeAsync(challengeToken, "ENROLL", ct);
        return await AsUserAsync(challenge.Scope, challenge.UserId, async tx =>
        {
            var user = await store.UserAsync(tx, challenge.UserId, ct) ?? throw ChallengeExpired();
            byte[] secret;
            if (challenge.PendingSecretEncrypted is not null)
            {
                // Asked again (a reload): show the same key, so a phone that already scanned it keeps working.
                secret = encryptor.Decrypt(challenge.PendingSecretEncrypted, ChallengeBinding(challenge.Id));
            }
            else
            {
                secret = Totp.NewSecret();
                await store.SetPendingSecretAsync(tx, challenge.Id, encryptor.Encrypt(secret, ChallengeBinding(challenge.Id)), ct);
            }
            return new Enrollment(Totp.Base32(secret), Totp.OtpauthUri(TotpIssuer, user.Email, secret));
        }, ct);
    }

    public async Task<SignInOutcome> ConfirmEnrollmentAsync(string? challengeToken, string? code, HttpContext request, CancellationToken ct)
    {
        var challenge = await LiveChallengeAsync(challengeToken, "ENROLL", ct);
        if (challenge.PendingSecretEncrypted is null)
        {
            throw ChallengeExpired();
        }
        var secret = encryptor.Decrypt(challenge.PendingSecretEncrypted, ChallengeBinding(challenge.Id));
        var step = Totp.MatchingStep(secret, code, clock.GetUtcNow().ToUnixTimeSeconds());
        if (step is null)
        {
            throw await WrongCodeAsync(challenge, ct);
        }
        return await AsUserAsync(challenge.Scope, challenge.UserId, async tx =>
        {
            var now = clock.GetUtcNow();
            if (!await store.EnableMfaAsync(tx, challenge.UserId, encryptor.Encrypt(secret, UserBinding(challenge.UserId)), step.Value, now, ct))
            {
                throw ChallengeExpired(); // enrolled meanwhile through another challenge
            }
            var user = await store.UserAsync(tx, challenge.UserId, ct) ?? throw ChallengeExpired();
            await audit.RecordAsync(tx, new AuditActor(user.Id, user.Role), "MFA_ENROLLED", "USER", user.Id, user.Scope, null, ct);
            return await CompleteAsync(tx, challenge, now, request, ct);
        }, ct);
    }

    /// <summary>One-time creation of the first Super Admin (contract §4).</summary>
    public async Task<UserView> BootstrapAsync(string? setupToken, string? rawEmail, string fullName, string password, HttpContext request,
        CancellationToken ct)
    {
        var security = options.Value.Security;
        // Hashing both sides first makes the comparison constant-time whatever the lengths.
        var tokenMatches = security.BootstrapEnabled() && SecureTokens.ConstantTimeEquals(
            SecureTokens.Sha256Hex(security.BootstrapToken!), SecureTokens.Sha256Hex(setupToken ?? "null"));
        if (!tokenMatches || await Unscoped(tx => store.SuperAdminExistsAsync(tx, ct), ct))
        {
            throw SetupNotAvailable();
        }
        var email = NormalizeEmail(rawEmail);
        if (PasswordPolicy.Check(password, email) is { Error: { } weak } refusal)
        {
            throw new ApiException(refusal.Status, weak.Code, weak.Message, weak.Field, weak.Params);
        }
        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        var hash = passwords.Hash(password);
        var created = await Unscoped(tx => store.BootstrapSuperAdminAsync(tx, id, email, JavaTrim(fullName), hash, clock.GetUtcNow(),
            Guid.CreateVersion7(now), RequestIds.Current(request), ClientInfo.PrefixOf(request.Connection.RemoteIpAddress), ct), ct);
        if (!created)
        {
            throw SetupNotAvailable();
        }
        return await AsUserAsync(Scope.Platform, id, async tx => await store.UserAsync(tx, id, ct)
            ?? throw new InvalidOperationException("The Super Admin just created is not readable"), ct);
    }

    private async Task<SignInOutcome> CompleteAsync(DbTx tx, Challenge challenge, DateTimeOffset now, HttpContext request, CancellationToken ct)
    {
        if (!await store.UseChallengeAsync(tx, challenge.Id, now, ct))
        {
            throw ChallengeExpired();
        }
        var user = await store.UserAsync(tx, challenge.UserId, ct) ?? throw ChallengeExpired();
        if (user.Status != UserStatus.ACTIVE)
        {
            throw Disabled();
        }
        return new SignInOutcome.Authenticated(await sessions.StartAsync(tx, user.Id, user.Role, user.Scope, request, ct));
    }

    private async Task<SignInOutcome> ChallengeAsync(DbTx tx, Guid userId, Scope scope, string purpose, HttpContext request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var lifetime = purpose == "VERIFY" ? VerifyChallengeLifetime : EnrollChallengeLifetime;
        var token = SecureTokens.Random(32);
        await store.InsertChallengeAsync(tx, Guid.CreateVersion7(now), userId, purpose, scope, SecureTokens.Sha256Hex(token), now, now + lifetime,
            ClientInfo.UserAgent(request), ClientInfo.PrefixOf(request.Connection.RemoteIpAddress), ct);
        var seconds = (long)lifetime.TotalSeconds;
        return purpose == "VERIFY" ? new SignInOutcome.MfaRequired(token, seconds) : new SignInOutcome.EnrollmentRequired(token, seconds);
    }

    private async Task<Challenge> LiveChallengeAsync(string? token, string purpose, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw ChallengeExpired();
        }
        var challenge = await Unscoped(tx => store.ChallengeByTokenHashAsync(tx, SecureTokens.Sha256Hex(token), ct), ct)
            ?? throw ChallengeExpired();
        var live = challenge.Purpose == purpose && challenge.UsedAt is null && challenge.ExpiresAt > clock.GetUtcNow()
            && challenge.Attempts < MaxChallengeAttempts;
        return live ? challenge : throw ChallengeExpired();
    }

    /// <summary>Counts a wrong code in its own transaction, so the count survives the refusal.</summary>
    private async Task<ApiException> WrongCodeAsync(Challenge challenge, CancellationToken ct)
    {
        await AsUserAsync(challenge.Scope, challenge.UserId, async tx =>
        {
            var attempts = await store.CountChallengeAttemptAsync(tx, challenge.Id, ct);
            await audit.RecordAsync(tx, AuditActor.Anonymous, "MFA_FAILED", "USER", challenge.UserId, challenge.Scope,
                new Dictionary<string, object?> { ["attempt"] = attempts, ["purpose"] = challenge.Purpose }, ct);
            return true;
        }, ct);
        return new ApiException(401, "MFA_INVALID_CODE", "The code is not correct");
    }

    /// <summary>The counter, the lock and their audit rows, committed in their own transaction through the V3 door.</summary>
    private Task<DateTimeOffset?> RecordFailureAsync(LoginCandidate user, bool count, string reason, HttpContext request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return Unscoped(tx => store.RecordLoginFailureAsync(tx, user.Id, count, reason, MaxFailedLogins, (int)Lock.TotalSeconds, now,
            Guid.CreateVersion7(now), Guid.CreateVersion7(now), RequestIds.Current(request),
            ClientInfo.PrefixOf(request.Connection.RemoteIpAddress), ct), ct);
    }

    private Task<T> Unscoped<T>(Func<DbTx, Task<T>> work, CancellationToken ct) => transactions.RunAsync(DbScopeContext.None, work, ct: ct);

    /// <summary>
    /// Runs <paramref name="work"/> in a transaction scoped to the user just identified (row-level security).
    /// Escalation, reviewed: only after the password matched, or a live challenge issued after it was presented,
    /// and the scope is the one the user's row holds.
    /// </summary>
    private Task<T> AsUserAsync<T>(Scope scope, Guid userId, Func<DbTx, Task<T>> work, CancellationToken ct) =>
        transactions.RunAsync(DbScopeContext.Escalate(scope, userId), work, ct: ct);

    /// <summary>Binds an encrypted TOTP secret to its user: copied into another row, it does not decrypt.</summary>
    public static byte[] UserBinding(Guid userId) => [.. "user"u8, .. userId.ToByteArray(bigEndian: true)];

    /// <summary>Binds a pending (enrolment) TOTP secret to its challenge.</summary>
    public static byte[] ChallengeBinding(Guid challengeId) => [.. "chal"u8, .. challengeId.ToByteArray(bigEndian: true)];

    public static string NormalizeEmail(string? email)
    {
        var normalized = email is null ? "" : JavaTrim(email).ToLowerInvariant();
        if (normalized.Length == 0 || normalized.Length > 254 || !normalized.Contains('@'))
        {
            // Shaped like a wrong password, so a malformed email reveals nothing either.
            throw InvalidCredentials();
        }
        return normalized;
    }

    /// <summary>Java's <c>String.trim</c>: strips characters up to U+0020, nothing else.</summary>
    public static string JavaTrim(string value) => value.Trim(TrimmedByJava);

    private static readonly char[] TrimmedByJava = Enumerable.Range(0, 0x21).Select(c => (char)c).ToArray();

    private static ApiException InvalidCredentials() => new(401, "INVALID_CREDENTIALS", "The email or password is not correct");

    private static ApiException Disabled() => new(403, "ACCOUNT_DISABLED", "This account is not active");

    private static ApiException SetupNotAvailable() => new(404, "SETUP_NOT_AVAILABLE", "Setup is not available");

    private static ApiException Locked(DateTimeOffset until, DateTimeOffset now)
    {
        var seconds = Math.Max(1L, (until - now).Ticks / TimeSpan.TicksPerSecond); // Duration.toSeconds: whole seconds
        return new ApiException(423, "ACCOUNT_LOCKED", "Too many failed attempts", null,
            new Dictionary<string, object> { ["retryAfterSeconds"] = seconds });
    }

    private static ApiException ChallengeExpired() => new(401, "MFA_CHALLENGE_EXPIRED", "Sign in again");
}
