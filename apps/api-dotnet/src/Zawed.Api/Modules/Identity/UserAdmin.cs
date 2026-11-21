using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Platform.Crypto;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Identity;

public sealed record CreatedUser(UserView User, string TemporaryPassword);

/// <summary>
/// User management by Super Admins and, for a narrow set of actions on company and station users, by Ops (contract
/// §4; Java <c>UserAdmin</c>). Every change is audited in the request's transaction, with the reason when one is
/// required.
/// <para>Ops never sees platform users: to Ops, a Super Admin or another Ops account does not exist (404), so it
/// cannot reset an administrator's password and take the account over.</para>
/// </summary>
public sealed class UserAdmin(IdentityStore store, PasswordHasher passwords, IAuditLog audit, IDbSession db, TimeProvider clock)
{
    public const int TemporaryPasswordLength = 16;

    public async Task<Result<Page<UserView>>> ListAsync(ZawedPrincipal actor, Role? role, UserStatus? status, string? query, string? cursor,
        int? limit, CancellationToken ct)
    {
        var visible = VisibleRoles(actor).Where(r => role is null || r == role).ToList();
        if (visible.Count == 0)
        {
            return new Page<UserView>([], null);
        }
        var size = Page<UserView>.Limit(limit);
        var rows = await store.ListUsersAsync(db.Current, visible, status, query, Cursors.Parse(cursor), size + 1, ct);
        return Page<UserView>.Of(rows, size, u => u.Id);
    }

    public async Task<Result<CreatedUser>> CreateAsync(ZawedPrincipal actor, string? rawEmail, string fullName, Role role, Scope scope,
        CancellationToken ct)
    {
        var tx = db.Current;
        if (scope.Type != role.ScopeType())
        {
            return Result<CreatedUser>.Fail(400, "SCOPE_MISMATCH", $"A {role} user needs a {role.ScopeType()} scope", "scope");
        }
        if (!await store.ScopeExistsAsync(tx, scope, ct))
        {
            return Result<CreatedUser>.Fail(400, "SCOPE_NOT_FOUND", $"No {scope.Type.ToString().ToLowerInvariant()} with this id", "scope");
        }
        var email = SignIn.NormalizeEmail(rawEmail);
        if (await store.EmailTakenAsync(tx, email, ct))
        {
            return EmailInUse<CreatedUser>();
        }
        var temporary = SecureTokens.TemporaryPassword(TemporaryPasswordLength);
        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        try
        {
            await tx.SavepointAsync("insert_user", () => store.InsertUserAsync(tx, id, email, SignIn.JavaTrim(fullName), passwords.Hash(temporary),
                role, scope, actor.UserId, now, ct), ct);
        }
        catch (Exception ex) when (DbErrors.IsUniqueViolation(ex))
        {
            return EmailInUse<CreatedUser>();
        }
        await audit.RecordAsync(tx, ActorOf(actor), "USER_CREATED", "USER", id, scope,
            new Dictionary<string, object?> { ["role"] = role.ToString() }, ct);
        return Result<CreatedUser>.Created(new CreatedUser((await store.UserAsync(tx, id, ct))!, temporary));
    }

    /// <summary>A new temporary password, shown once. Ends every session of the user.</summary>
    public async Task<Result<string>> ResetPasswordAsync(ZawedPrincipal actor, Guid userId, CancellationToken ct)
    {
        var tx = db.Current;
        if (await VisibleAsync(actor, userId, ct) is not { } user)
        {
            return Result<string>.NotFound("User");
        }
        var temporary = SecureTokens.TemporaryPassword(TemporaryPasswordLength);
        var now = clock.GetUtcNow();
        await store.SetPasswordAsync(tx, user.Id, passwords.Hash(temporary), true, actor.UserId, now, ct);
        await store.VoidChallengesOfAsync(tx, user.Id, now, ct);
        var ended = await store.RevokeSessionsOfAsync(tx, user.Id, null, "PASSWORD_RESET", now, ct);
        await audit.RecordAsync(tx, ActorOf(actor), "PASSWORD_RESET", "USER", user.Id, user.Scope,
            new Dictionary<string, object?> { ["sessionsEnded"] = ended }, ct);
        return temporary;
    }

    public async Task<Result> UnlockAsync(ZawedPrincipal actor, Guid userId, CancellationToken ct)
    {
        var tx = db.Current;
        if (await VisibleAsync(actor, userId, ct) is not { } user)
        {
            return Result.NotFound("User");
        }
        await store.UnlockAsync(tx, user.Id, actor.UserId, clock.GetUtcNow(), ct);
        await audit.RecordAsync(tx, ActorOf(actor), "ACCOUNT_UNLOCKED", "USER", user.Id, user.Scope, null, ct);
        return Result.NoContent();
    }

    public async Task<Result> SetStatusAsync(ZawedPrincipal actor, Guid userId, UserStatus status, string reason, CancellationToken ct)
    {
        var tx = db.Current;
        if (await VisibleAsync(actor, userId, ct) is not { } user)
        {
            return Result.NotFound("User");
        }
        if (user.Id == actor.UserId)
        {
            return Result.Conflict("SELF_CHANGE_NOT_ALLOWED", "You cannot change your own status");
        }
        if (user.Role == Role.SUPER_ADMIN && user.Status == UserStatus.ACTIVE && status != UserStatus.ACTIVE
            && await store.LockActiveSuperAdminsAsync(tx, ct) <= 1)
        {
            return Result.Conflict("LAST_SUPER_ADMIN", "The last active Super Admin must stay active");
        }
        var now = clock.GetUtcNow();
        await store.SetStatusAsync(tx, user.Id, status, actor.UserId, now, ct);
        if (status != UserStatus.ACTIVE)
        {
            await store.VoidChallengesOfAsync(tx, user.Id, now, ct);
        }
        var ended = status == UserStatus.ACTIVE ? 0 : await store.RevokeSessionsOfAsync(tx, user.Id, null, "STATUS_" + status, now, ct);
        await audit.RecordAsync(tx, ActorOf(actor), "USER_STATUS_CHANGED", "USER", user.Id, user.Scope, new Dictionary<string, object?>
        {
            ["from"] = user.Status.ToString(), ["to"] = status.ToString(), ["reason"] = reason, ["sessionsEnded"] = ended,
        }, ct);
        return Result.NoContent();
    }

    public async Task<Result> ResetMfaAsync(ZawedPrincipal actor, Guid userId, string reason, CancellationToken ct)
    {
        var tx = db.Current;
        if (await VisibleAsync(actor, userId, ct) is not { } user)
        {
            return Result.NotFound("User");
        }
        var now = clock.GetUtcNow();
        await store.ResetMfaAsync(tx, user.Id, actor.UserId, now, ct);
        await store.VoidChallengesOfAsync(tx, user.Id, now, ct);
        var ended = await store.RevokeSessionsOfAsync(tx, user.Id, null, "MFA_RESET", now, ct);
        await audit.RecordAsync(tx, ActorOf(actor), "MFA_RESET", "USER", user.Id, user.Scope,
            new Dictionary<string, object?> { ["reason"] = reason, ["sessionsEnded"] = ended }, ct);
        return Result.NoContent();
    }

    /// <summary>The target user, or null (404) when the actor may not see it.</summary>
    private async Task<UserView?> VisibleAsync(ZawedPrincipal actor, Guid userId, CancellationToken ct) =>
        await store.UserAsync(db.Current, userId, ct) is { } user && VisibleRoles(actor).Contains(user.Role) ? user : null;

    private static IReadOnlyList<Role> VisibleRoles(ZawedPrincipal actor) => actor.Role switch
    {
        Role.SUPER_ADMIN => Enum.GetValues<Role>(),
        Role.OPS_MANAGER or Role.OPS => Enum.GetValues<Role>().Where(r => !r.IsPlatform()).ToList(),
        _ => [],
    };

    private static AuditActor ActorOf(ZawedPrincipal actor) => new(actor.UserId, actor.Role);

    private static Result<T> EmailInUse<T>() => Result<T>.Fail(409, "EMAIL_IN_USE", "This email is already used", "email");
}
