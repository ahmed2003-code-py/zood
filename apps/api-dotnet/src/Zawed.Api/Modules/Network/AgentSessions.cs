using System.Text.RegularExpressions;
using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Modules.Network.Contracts;
using Zawed.Api.Platform.Crypto;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.RateLimit;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Text;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Network;

/// <summary>The agent behind a request, verified against the session row on every call.</summary>
public sealed record AgentRef(Guid AgentId, string WorkerId, string FullName, bool MustChangePin);

public sealed record AgentSignedIn(string SessionToken, DateTimeOffset ExpiresAt, AgentRef Agent);

/// <summary>
/// An agent signed in on a device (PRD 6.1; Java <c>network.AgentSessions</c>): Worker ID and 6-digit PIN. A PIN has a
/// million values, so it is Argon2id-hashed, checked in constant time for unknown workers too, and locked after
/// <see cref="MaxFailures"/> wrong tries. The agent must work at the device's own station. One live session per agent:
/// signing in on another device ends the first (plan §6: no credential sharing).
/// <para>Runs under the device channel's explicit platform scope, like <see cref="DeviceChannel"/>, each step in a
/// transaction of its own. A refused attempt's counter and audit row commit; the refusal is thrown after the commit.</para>
/// </summary>
public sealed partial class AgentSessions(ITransactions transactions, PasswordHasher hasher, IAuditLog audit, IRateLimiter limiter,
    TimeProvider clock) : IAgentShifts
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Lock = TimeSpan.FromMinutes(15);

    /// <summary>A shift, with margin. The agent signs in again for the next one.</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    private const int SignInsPerMinute = 10;

    /// <summary>
    /// How far a capture's device time may fall outside the session it belongs to: the device clock may be this far from
    /// the server's (the same window a token request is allowed), and an offline terminal cannot correct its clock.
    /// </summary>
    public static readonly TimeSpan ClockTolerance = DeviceChannel.ClockWindow;

    private enum Outcome
    {
        Ok,
        Wrong,
        Locked,
        Disabled,
    }

    private sealed record Attempt(Outcome Outcome, AgentSignedIn? SignedIn, DateTimeOffset? LockedUntil);

    private sealed class AgentRefRow
    {
        public Guid Id { get; set; }
        public string Worker_Id { get; set; } = "";
        public string Full_Name { get; set; } = "";
        public bool Must_Change_Pin { get; set; }
    }

    private sealed class PinRow
    {
        public string Pin_Hash { get; set; } = "";
        public int Failed_Pin_Count { get; set; }
        public DateTime? Locked_Until { get; set; }
    }

    private sealed class OnDeviceRow
    {
        public Guid Id { get; set; }
        public string Worker_Id { get; set; } = "";
    }

    [GeneratedRegex("^[0-9]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex SixDigits();

    public async Task<AgentSignedIn> SignInAsync(DeviceRef device, string? workerId, string? pin, CancellationToken ct)
    {
        limiter.Acquire("pos-agent:" + device.DeviceId, SignInsPerMinute, TimeSpan.FromMinutes(1));
        var worker = workerId is null ? "" : JavaText.ToUpperRoot(Rows.JavaStrip(workerId));
        var secret = pin is null ? "" : Rows.JavaStrip(pin);
        var now = clock.GetUtcNow();
        // One transaction holding the agent row: failures are counted and committed (the outcome is returned, not
        // thrown), and a successful sign-in ends the old session and opens the new one under the same lock, so two
        // sign-ins at once cannot both succeed.
        var attempt = await transactions.RunAsync(Platform(), async tx =>
        {
            var agent = await tx.QuerySingleOrDefaultAsync<AgentRow>("select * from agents where worker_id = @worker for update",
                new { worker }, ct);
            if (agent is null || agent.Station_Id != device.StationId)
            {
                // Same work as a real check: the response time does not reveal whether a worker exists or works elsewhere.
                hasher.Burn(secret);
                return new Attempt(Outcome.Wrong, null, null);
            }
            if (Rows.Utc(agent.Locked_Until) is { } lockedUntil && lockedUntil > now)
            {
                hasher.Burn(secret);
                return new Attempt(Outcome.Locked, null, lockedUntil);
            }
            if (!hasher.Matches(secret, agent.Pin_Hash))
            {
                var failures = agent.Failed_Pin_Count + 1;
                DateTimeOffset? locked = failures >= MaxFailures ? now + Lock : null;
                await tx.ExecuteAsync("update agents set failed_pin_count = @count, locked_until = @locked where id = @id",
                    new { count = locked is null ? failures : 0, locked, id = agent.Id }, ct);
                await audit.RecordAsync(tx, AuditActor.Anonymous, locked is null ? "AGENT_PIN_FAILED" : "AGENT_LOCKED", "AGENT", agent.Id,
                    Scope.Operator(device.OperatorId), new Dictionary<string, object?> { ["deviceId"] = device.DeviceId.ToString() }, ct);
                return new Attempt(locked is null ? Outcome.Wrong : Outcome.Locked, null, locked);
            }
            if (agent.AgentStatus != AgentStatus.ACTIVE)
            {
                return new Attempt(Outcome.Disabled, null, null);
            }
            return new Attempt(Outcome.Ok, await OpenAsync(tx, device, agent, now, ct), null);
        }, ct: ct);

        return attempt.Outcome switch
        {
            Outcome.Wrong => throw new ApiException(401, "AGENT_SIGN_IN_FAILED", "The worker ID or PIN is not correct"),
            Outcome.Locked => throw LockedOut(now, attempt.LockedUntil!.Value),
            Outcome.Disabled => throw new ApiException(403, "AGENT_DISABLED", "This agent cannot sign in"),
            _ => attempt.SignedIn!,
        };
    }

    private async Task<AgentSignedIn> OpenAsync(DbTx tx, DeviceRef device, AgentRow agent, DateTimeOffset now, CancellationToken ct)
    {
        var token = SecureTokens.Random(32);
        var expires = DeviceTokens.Micros(now) + SessionLifetime;
        await tx.ExecuteAsync("update agents set failed_pin_count = 0, locked_until = null where id = @id", new { id = agent.Id }, ct);
        var ended = await tx.ExecuteAsync("""
            update agent_sessions set revoked_at = @now, revoke_reason = 'SIGNED_IN_ELSEWHERE'
            where agent_id = @agent and revoked_at is null
            """, new { agent = agent.Id, now }, ct);
        await tx.ExecuteAsync("""
            insert into agent_sessions (id, agent_id, device_id, operator_id, station_id, token_hash, created_at,
                                        last_seen_at, expires_at)
            values (@id, @agent, @device, @operator, @station, @hash, @now, @now, @expires)
            """, new
        {
            id = Guid.CreateVersion7(now), agent = agent.Id, device = device.DeviceId, @operator = device.OperatorId, station = device.StationId,
            hash = SecureTokens.Sha256Hex(token), now, expires,
        }, ct);
        await audit.RecordAsync(tx, AuditActor.Anonymous, "AGENT_SIGNED_IN", "AGENT", agent.Id, Scope.Operator(device.OperatorId),
            new Dictionary<string, object?> { ["deviceId"] = device.DeviceId.ToString(), ["sessionsEnded"] = ended }, ct);
        return new AgentSignedIn(token, expires, new AgentRef(agent.Id, agent.Worker_Id, agent.Full_Name, agent.Must_Change_Pin));
    }

    /// <summary>
    /// The agent of a live session on this device: not revoked, not expired, the agent still ACTIVE and still at the
    /// device's station. Read on every request, so suspension is immediate.
    /// </summary>
    public async Task<AgentRef?> VerifyAsync(DeviceRef device, string? sessionToken, CancellationToken ct)
    {
        if (Rows.JavaBlank(sessionToken))
        {
            return null;
        }
        var now = clock.GetUtcNow();
        var row = await transactions.RunAsync(Platform(), tx => tx.QuerySingleOrDefaultAsync<AgentRefRow>("""
            select a.id, a.worker_id, a.full_name, a.must_change_pin
            from agent_sessions s join agents a on a.id = s.agent_id
            where s.token_hash = @hash and s.device_id = @device and s.revoked_at is null and s.expires_at > @now
              and a.status = 'ACTIVE' and a.station_id = @station
            """, new { hash = SecureTokens.Sha256Hex(sessionToken!), device = device.DeviceId, station = device.StationId, now }, ct), ct: ct);
        return row is null ? null : new AgentRef(row.Id, row.Worker_Id, row.Full_Name, row.Must_Change_Pin);
    }

    /// <summary>
    /// B5 contract §3: the agent of an offline capture, from the session history. Sessions are never deleted, so the
    /// answer for a past instant does not change when the agent signs out, is suspended or moves on later.
    /// </summary>
    public async Task<AgentOnDevice?> OfStationAsync(DbTx tx, Guid stationId, Guid agentId, CancellationToken ct = default) =>
        await tx.QuerySingleOrDefaultAsync<OnDeviceRow>("select id, worker_id from agents where id = @agent and station_id = @station",
            new { agent = agentId, station = stationId }, ct) is { } row ? new AgentOnDevice(row.Id, row.Worker_Id) : null;

    public async Task<AgentOnDevice?> OnDeviceAtAsync(DbTx tx, Guid deviceId, Guid stationId, Guid agentId, DateTimeOffset at,
        CancellationToken ct = default) =>
        await tx.QuerySingleOrDefaultAsync<OnDeviceRow>("""
            select a.id, a.worker_id from agents a
            where a.id = @agent and a.station_id = @station
              and exists (select 1 from agent_sessions s
                          where s.agent_id = a.id and s.device_id = @device
                            and s.created_at <= @latest
                            and (s.revoked_at is null or s.revoked_at > @earliest)
                            and s.expires_at > @earliest)
            """, new { agent = agentId, station = stationId, device = deviceId, latest = at + ClockTolerance, earliest = at - ClockTolerance }, ct)
            is { } row ? new AgentOnDevice(row.Id, row.Worker_Id) : null;

    /// <summary>
    /// The agent replaces the PIN Ops gave them with their own (forced at first sign-in). A wrong current PIN counts
    /// against the same lockout as sign-in, so a signed-in session is not a way to guess PINs (B4 review 4); when the
    /// lock trips, every session of the agent ends.
    /// </summary>
    public async Task ChangePinAsync(DeviceRef device, AgentRef agent, string? currentPin, string? newPin, CancellationToken ct)
    {
        limiter.Acquire("pos-pin:" + agent.AgentId, SignInsPerMinute, TimeSpan.FromMinutes(1));
        var next = newPin is null ? "" : Rows.JavaStrip(newPin);
        if (!SixDigits().IsMatch(next) || Agents.IsTrivial(next))
        {
            throw new ApiException(400, "PIN_TOO_WEAK", "A PIN is 6 digits and not one digit repeated or a straight run", "newPin");
        }
        var current = currentPin is null ? "" : Rows.JavaStrip(currentPin);
        var now = clock.GetUtcNow();
        var change = await transactions.RunAsync(Platform(), async tx =>
        {
            var row = await tx.QuerySingleAsync<PinRow>("select pin_hash, failed_pin_count, locked_until from agents where id = @id for update",
                new { id = agent.AgentId }, ct);
            if (Rows.Utc(row.Locked_Until) is { } lockedUntil && lockedUntil > now)
            {
                hasher.Burn(current);
                return new Attempt(Outcome.Locked, null, lockedUntil);
            }
            if (!hasher.Matches(current, row.Pin_Hash))
            {
                var failures = row.Failed_Pin_Count + 1;
                DateTimeOffset? locked = failures >= MaxFailures ? now + Lock : null;
                await tx.ExecuteAsync("update agents set failed_pin_count = @count, locked_until = @locked where id = @id",
                    new { count = locked is null ? failures : 0, locked, id = agent.AgentId }, ct);
                if (locked is not null)
                {
                    await EndSessionsAsync(tx, agent.AgentId, "LOCKED", now, ct);
                }
                await audit.RecordAsync(tx, AuditActor.Anonymous, locked is null ? "AGENT_PIN_FAILED" : "AGENT_LOCKED", "AGENT", agent.AgentId,
                    Scope.Operator(device.OperatorId),
                    new Dictionary<string, object?> { ["deviceId"] = device.DeviceId.ToString(), ["during"] = "PIN_CHANGE" }, ct);
                return new Attempt(locked is null ? Outcome.Wrong : Outcome.Locked, null, locked);
            }
            if (hasher.Matches(next, row.Pin_Hash))
            {
                throw new ApiException(400, "PIN_REUSED", "Choose a PIN different from the current one", "newPin");
            }
            await tx.ExecuteAsync("""
                update agents set pin_hash = @hash, must_change_pin = false, failed_pin_count = 0, locked_until = null,
                       updated_at = @now
                where id = @id
                """, new { hash = hasher.Hash(next), now, id = agent.AgentId }, ct);
            await audit.RecordAsync(tx, AuditActor.Anonymous, "AGENT_PIN_CHANGED", "AGENT", agent.AgentId, Scope.Operator(device.OperatorId),
                new Dictionary<string, object?> { ["deviceId"] = device.DeviceId.ToString() }, ct);
            return new Attempt(Outcome.Ok, null, null);
        }, ct: ct);
        switch (change.Outcome)
        {
            case Outcome.Wrong:
                throw new ApiException(401, "AGENT_SIGN_IN_FAILED", "The current PIN is not correct", "currentPin");
            case Outcome.Locked:
                throw LockedOut(now, change.LockedUntil!.Value);
        }
    }

    /// <summary>Ends every live session of the agent, in the caller's transaction.</summary>
    public static Task<int> EndSessionsAsync(DbTx tx, Guid agentId, string reason, DateTimeOffset now, CancellationToken ct) => tx.ExecuteAsync("""
        update agent_sessions set revoked_at = @now, revoke_reason = @reason
        where agent_id = @agent and revoked_at is null
        """, new { agent = agentId, reason, now }, ct);

    /// <summary>Records the session's last use, at most once a minute (shown to the station as "last seen").</summary>
    public Task TouchAsync(string sessionToken, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return transactions.RunAsync(Platform(), tx => tx.ExecuteAsync("""
            update agent_sessions set last_seen_at = @now
            where token_hash = @hash and revoked_at is null and last_seen_at < @stale
            """, new { hash = SecureTokens.Sha256Hex(sessionToken), now, stale = now - TimeSpan.FromMinutes(1) }, ct), ct: ct);
    }

    public Task SignOutAsync(string sessionToken, CancellationToken ct) =>
        transactions.RunAsync(Platform(), tx => tx.ExecuteAsync("""
            update agent_sessions set revoked_at = @now, revoke_reason = 'SIGNED_OUT'
            where token_hash = @hash and revoked_at is null
            """, new { hash = SecureTokens.Sha256Hex(sessionToken), now = clock.GetUtcNow() }, ct), ct: ct);

    /// <summary>Java <c>Duration.between(now, lockedUntil).toSeconds()</c>, at least one.</summary>
    private static ApiException LockedOut(DateTimeOffset now, DateTimeOffset lockedUntil) => new(423, "AGENT_LOCKED", "Too many wrong PINs", null,
        new Dictionary<string, object> { ["retryAfterSeconds"] = Math.Max(1L, (lockedUntil - now).Ticks / TimeSpan.TicksPerSecond) });

    /// <summary>The platform scope of the device channel, entered after the device (and, where named, the agent) is verified.</summary>
    private static DbScopeContext Platform() => DbScopeContext.Escalate(Scope.Platform, null);
}
