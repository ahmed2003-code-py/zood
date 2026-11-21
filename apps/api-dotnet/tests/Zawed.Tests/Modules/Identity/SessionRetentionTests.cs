using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zawed.Api.Modules.Identity;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Support;
using static Zawed.Tests.Support.IdentityTestSupport;

namespace Zawed.Tests.Modules.Identity;

/// <summary>
/// Web sessions and MFA challenges are purged after their retention (V21, ADR 0028), through bounded SECURITY DEFINER
/// functions with their own floors, and never touch a live session, nor the station agents' sessions that shifts are
/// built from (Java <c>SessionRetentionTests</c>).
/// </summary>
public sealed class SessionRetentionTests(SharedApi api) : IClassFixture<SharedApi>
{
    private const string Refresh = "/api/v1/auth/refresh";

    private readonly IdentityTestSupport id = new(api.Factory);

    private SessionRetention Retention => api.Factory.Services.GetRequiredService<SessionRetention>();

    // ---------------------------------------------------------------------------------------- sessions

    [Fact]
    public async Task OnlySessionsThatEndedBeforeTheCutoffArePurgedAndALiveOneNeverIsHoweverOld()
    {
        var user = (await id.SeedAsync(Role.FLEET_MANAGER)).Id;
        const string justOver = "now() - interval '31 days 1 minute'";
        const string justUnder = "now() - interval '30 days 23 hours 59 minutes'";
        const string future = "now() + interval '1 hour'";
        // Live: signed in 400 days ago (impossible with a 30-day absolute end, so the strongest case).
        var live = await PlantSessionAsync(user, "now() - interval '400 days'", future, future, null);
        var revokedOver = await PlantSessionAsync(user, "now() - interval '40 days'", future, future, justOver);
        var revokedUnder = await PlantSessionAsync(user, "now() - interval '40 days'", future, future, justUnder);
        var idleOver = await PlantSessionAsync(user, "now() - interval '40 days'", justOver, future, null);
        var idleUnder = await PlantSessionAsync(user, "now() - interval '40 days'", justUnder, future, null);
        var absoluteOver = await PlantSessionAsync(user, "now() - interval '60 days'", future, justOver, null);
        // Idle-expired long ago, then revoked yesterday: it ended at the earlier of the two.
        var expiredThenRevoked = await PlantSessionAsync(user, "now() - interval '120 days'", "now() - interval '100 days'",
            "now() - interval '90 days'", "now() - interval '1 day'");
        var usableBefore = await UsableSessionsAsync();

        await using (var app = await AppAsync(null))
        {
            await app.Connection.ExecuteAsync("select purge_sessions(now() - interval '31 days', 50000)", transaction: app.Transaction);
            await app.Transaction.CommitAsync();
        }

        Assert.Equal(new[] { live, revokedUnder, idleUnder }.Order(), (await SessionsOfAsync(user)).Order());
        Assert.DoesNotContain(revokedOver, await SessionsOfAsync(user));
        Assert.DoesNotContain(idleOver, await SessionsOfAsync(user));
        Assert.DoesNotContain(absoluteOver, await SessionsOfAsync(user));
        Assert.DoesNotContain(expiredThenRevoked, await SessionsOfAsync(user));
        // The property itself, over the whole table: nothing that Sessions.UsableAt accepts was deleted.
        Assert.Equal(usableBefore.Order(), (await ExistingAsync(usableBefore)).Order());
    }

    [Fact]
    public async Task TheJobUsesTheNinetyDayDefaultAndNeverAsksForLessThanTheFloorPlusADay()
    {
        var user = (await id.SeedAsync(Role.ACCOUNTANT)).Id;
        var old = await PlantSessionAsync(user, "now() - interval '120 days'", "now() - interval '91 days'", "now() - interval '90 days'", null);
        var kept = await PlantSessionAsync(user, "now() - interval '120 days'", "now() - interval '89 days'", "now() - interval '89 days'",
            "now() - interval '89 days'");

        await Retention.PurgeSessionsAsync();
        Assert.Equal([kept], await SessionsOfAsync(user));
        Assert.DoesNotContain(old, await SessionsOfAsync(user));

        Assert.Equal(TimeSpan.FromDays(30), SessionRetention.SessionFloor);
        Assert.Equal(TimeSpan.FromDays(31), SessionRetention.AtLeast(TimeSpan.FromDays(1), SessionRetention.SessionMinimum));
        Assert.Equal(TimeSpan.FromDays(90), SessionRetention.AtLeast(TimeSpan.FromDays(90), SessionRetention.SessionMinimum));
        Assert.Equal(TimeSpan.FromHours(2), SessionRetention.AtLeast(TimeSpan.FromMinutes(5), SessionRetention.ChallengeMinimum));
    }

    [Fact]
    public async Task TheFunctionsRefuseACutoffInsideTheirFloorWhoeverCallsThem()
    {
        string[][] refused =
        [
            ["purge_sessions", "now() - interval '29 days'", "30-day minimum"],
            ["purge_sessions", "now() - interval '29 days 23 hours'", "30-day minimum"],
            ["purge_sessions", "null", "30-day minimum"],
            ["purge_sessions", "now() + interval '1 day'", "30-day minimum"],
            ["purge_mfa_challenges", "now() - interval '59 minutes'", "1-hour minimum"],
            ["purge_mfa_challenges", "null", "1-hour minimum"],
        ];
        foreach (var call in refused)
        {
            foreach (var scope in new[] { "PLATFORM", "COMPANY", null })
            {
                await using var app = await AppAsync(scope);
                var ex = await Assert.ThrowsAsync<PostgresException>(() =>
                    app.Connection.ExecuteAsync($"select {call[0]}({call[1]}, 100)", transaction: app.Transaction));
                Assert.Contains(call[2], ex.Message, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task TheRuntimeRoleCannotDeleteSessionsChallengesOrAgentSessionsDirectly()
    {
        foreach (var table in new[] { "sessions", "mfa_challenges", "agent_sessions", "job_runs" })
        {
            Assert.False(await id.Owner.QueryOneAsync<bool>("select has_table_privilege('zawed_app', cast(@table as text), 'DELETE')", new { table }));
            foreach (var scope in new[] { "PLATFORM", "COMPANY", null })
            {
                await using var app = await AppAsync(scope);
                var ex = await Assert.ThrowsAsync<PostgresException>(() =>
                    app.Connection.ExecuteAsync($"delete from {table} where false", transaction: app.Transaction));
                Assert.Contains("permission denied", ex.Message, StringComparison.Ordinal);
            }
        }
    }

    // ---------------------------------------------------------------------------------- agent sessions

    [Fact]
    public async Task AgentSessionsAreNeverPurgedWhateverTheirAge()
    {
        // Planted with foreign keys switched off for this connection only, and removed afterwards: the rows only need to
        // look like shifts that ended over a year ago.
        var planted = new List<Guid>();
        try
        {
            await using (var owner = new NpgsqlConnection(id.Owner.ConnectionString))
            {
                await owner.OpenAsync();
                await owner.ExecuteAsync("set session_replication_role = replica");
                foreach (var revoked in new[] { "now() - interval '400 days'", "null" })
                {
                    var agentSession = Guid.CreateVersion7();
                    planted.Add(agentSession);
                    await owner.ExecuteAsync($"""
                        insert into agent_sessions (id, agent_id, device_id, operator_id, station_id, token_hash,
                                                    created_at, last_seen_at, expires_at, revoked_at, revoke_reason)
                        values (@id, gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(),
                                'agent-retention-' || @id, now() - interval '401 days', now() - interval '401 days',
                                now() - interval '400 days', {revoked}, {(revoked == "null" ? "null" : "'SIGNED_OUT'")})
                        """, new { id = agentSession });
                }
            }
            await using (var app = await AppAsync(null))
            {
                await app.Connection.ExecuteAsync("select purge_sessions(now() - interval '31 days', 50000)", transaction: app.Transaction);
                await app.Connection.ExecuteAsync("select purge_mfa_challenges(now() - interval '2 hours', 50000)", transaction: app.Transaction);
                await app.Transaction.CommitAsync();
            }
            await Retention.RunNowAsync();

            Assert.Equal(2L, await id.Owner.QueryOneAsync<long>("select count(*) from agent_sessions where id = any(@ids)", new { ids = planted.ToArray() }));
            // And by construction: neither function names the table.
            foreach (var function in new[] { "purge_sessions(timestamptz, integer)", "purge_mfa_challenges(timestamptz, integer)" })
            {
                var body = await id.Owner.QueryOneAsync<string>("select pg_get_functiondef(cast(@function as regprocedure))", new { function });
                Assert.DoesNotContain("agent_sessions", body!, StringComparison.OrdinalIgnoreCase);
            }
            Assert.Contains("DELETE FROM sessions s", (await id.Owner.QueryOneAsync<string>(
                "select pg_get_functiondef(cast('purge_sessions(timestamptz, integer)' as regprocedure))"))!, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var agentSession in planted)
            {
                await id.Owner.ExecuteAsync("delete from agent_sessions where id = @agentSession", new { agentSession });
            }
        }
    }

    // ---------------------------------------------------------------------------------------- challenges

    [Fact]
    public async Task ExpiredChallengesArePurgedAndLiveOnesKept()
    {
        var user = (await id.SeedAsync(Role.OPS)).Id;
        var old = await PlantChallengeAsync(user, "now() - interval '2 days'");
        var recent = await PlantChallengeAsync(user, "now() - interval '2 hours'");
        var live = await PlantChallengeAsync(user, "now() + interval '5 minutes'");
        var justExpired = await PlantChallengeAsync(user, "now() - interval '1 second'");

        await Retention.PurgeChallengesAsync();
        Assert.Equal(new[] { recent, live, justExpired }.Order(), (await ChallengesOfAsync(user)).Order());
        Assert.DoesNotContain(old, await ChallengesOfAsync(user));

        await using (var app = await AppAsync(null))
        {
            await app.Connection.ExecuteAsync("select purge_mfa_challenges(now() - interval '1 hour', 50000)", transaction: app.Transaction);
            await app.Transaction.CommitAsync();
        }
        Assert.Equal(new[] { live, justExpired }.Order(), (await ChallengesOfAsync(user)).Order());
    }

    [Fact]
    public async Task AnEnrolmentUnderWaySurvivesTheStrictestPurgeAndCompletes()
    {
        // A user who must enrol a second factor: the ENROLL challenge holds the pending secret.
        var ops = await id.SeedAsync(Role.OPS);
        await id.Owner.ExecuteAsync("update users set mfa_enabled = false, mfa_secret_encrypted = null where id = @Id", new { ops.Id });
        var login = await JsonOf(await id.PostAsync("/api/v1/auth/login", LoginBody(ops.Email, Password)));
        Assert.Equal("MFA_ENROLLMENT_REQUIRED", login["status"]!.GetValue<string>());
        var challenge = login["challengeToken"]!.GetValue<string>();
        var start = await id.PostAsync("/api/v1/auth/mfa/enroll/start", ChallengeBody(challenge));
        Assert.Equal(200, (int)start.StatusCode);
        Assert.Equal(1L, await id.Owner.QueryOneAsync<long>("""
            select count(*) from mfa_challenges
            where user_id = @Id and purpose = 'ENROLL' and pending_secret_encrypted is not null and used_at is null
            """, new { ops.Id }));

        // The strictest cutoff the database accepts, and the job itself.
        await using (var app = await AppAsync(null))
        {
            await app.Connection.ExecuteAsync("select purge_mfa_challenges(now() - interval '1 hour', 50000)", transaction: app.Transaction);
            await app.Transaction.CommitAsync();
        }
        await Retention.RunNowAsync();

        var secret = Base32Decode((await JsonOf(start))["secret"]!.GetValue<string>());
        var confirm = await id.PostAsync("/api/v1/auth/mfa/enroll/confirm", ChallengeCodeBody(challenge, Code(secret, 0)));
        Assert.Equal(200, (int)confirm.StatusCode);
        Assert.Equal("AUTHENTICATED", (await JsonOf(confirm))["status"]!.GetValue<string>());
    }

    // --------------------------------------------------------------------------------------- concurrency

    [Fact]
    public async Task RefreshesAndSignInsRacingThePurgeLoseNoLiveSession()
    {
        const int users = 4;
        var seeded = new List<SeededUser>();
        var sessions = new List<SignedIn>();
        for (var i = 0; i < users; i++)
        {
            var user = await id.SeedAsync(i % 2 == 0 ? Role.FLEET_MANAGER : Role.ACCOUNTANT);
            seeded.Add(user);
            sessions.Add(await id.SignInAsync(user));
        }
        // Plenty of dead rows next to the live ones, on the same users and the same indexes.
        foreach (var user in seeded)
        {
            await id.Owner.ExecuteAsync("""
                insert into sessions (id, user_id, door, refresh_token_hash, created_at, last_seen_at, expires_at,
                                      absolute_expires_at, revoked_at, revoked_reason)
                select gen_random_uuid(), @Id, 'USER', 'dead-' || gen_random_uuid(), now() - interval '150 days',
                       now() - interval '150 days', now() - interval '140 days', now() - interval '120 days',
                       case when g % 2 = 0 then now() - interval '145 days' end,
                       case when g % 2 = 0 then 'LOGOUT' end
                from generate_series(1, 600) g
                """, new { user.Id });
        }
        var usableBefore = await UsableSessionsAsync();
        id.Limiter.Reset();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, users + 4).Select(index => Task.Run(async () =>
        {
            if (index < users)
            {
                // A user refreshing again and again, each time with the cookie the last refresh set.
                var cookie = sessions[index].RefreshToken;
                string? accessToken = null;
                for (var round = 0; round < 8; round++)
                {
                    var refreshed = await id.WithCookieAsync(Refresh, "zawed_rt", cookie);
                    if ((int)refreshed.StatusCode != 200)
                    {
                        return $"refresh {(int)refreshed.StatusCode} {await refreshed.Content.ReadAsStringAsync()}";
                    }
                    cookie = Cookie(refreshed, "zawed_rt") ?? cookie;
                    accessToken = (await JsonOf(refreshed))["accessToken"]!.GetValue<string>();
                }
                var me = (int)(await id.AuthedAsync("GET", "/api/v1/auth/me", accessToken!)).StatusCode;
                return me == 200 ? "ok" : "me " + me;
            }
            if (index < users + 2)
            {
                // Two purgers in small batches, as two instances would run it.
                int deleted;
                do
                {
                    await using var app = await AppAsync(null);
                    deleted = await app.Connection.QuerySingleAsync<int>("select purge_sessions(now() - interval '31 days', 50)", transaction: app.Transaction);
                    await app.Transaction.CommitAsync();
                }
                while (deleted > 0);
                return "ok";
            }
            if (index == users + 2)
            {
                await Retention.RunNowAsync();
                return "ok";
            }
            // New sign-ins while the purge runs.
            for (var i = 0; i < 3; i++)
            {
                await id.SignInAsync(await id.SeedAsync(Role.FLEET_MANAGER));
            }
            return "ok";
        })));

        Assert.All(outcomes, o => Assert.Equal("ok", o));
        foreach (var user in seeded)
        {
            Assert.Equal(0L, await id.Owner.QueryOneAsync<long>(
                "select count(*) from sessions where user_id = @Id and refresh_token_hash like 'dead-%'", new { user.Id })); // dead sessions left
            Assert.Equal(1L, await id.Owner.QueryOneAsync<long>("""
                select count(*) from sessions
                where user_id = @Id and revoked_at is null and expires_at > now() and absolute_expires_at > now()
                """, new { user.Id })); // the user's live session
        }
        Assert.Equal(usableBefore.Order(), (await ExistingAsync(usableBefore)).Order());
    }

    // ------------------------------------------------------------------------------------------- the job

    [Fact]
    public async Task EachRunIsRecordedWithItsCounts()
    {
        await Retention.RunNowAsync();
        Assert.Equal("SUCCESS true true", await id.Owner.QueryOneAsync<string>("""
            select outcome || ' ' || (counts ->> 'sessions' ~ '^[0-9]+$') || ' ' || (counts ->> 'mfaChallenges' ~ '^[0-9]+$')
            from job_runs where job_name = 'session-retention' order by started_at desc, id desc limit 1
            """));
    }

    // ------------------------------------------------------------------------------------------- helpers

    /// <summary>Each argument is a SQL expression; <paramref name="revokedAt"/> may be null.</summary>
    private async Task<Guid> PlantSessionAsync(Guid user, string createdAt, string expiresAt, string absoluteExpiresAt, string? revokedAt)
    {
        var sessionId = Guid.CreateVersion7();
        await id.Owner.ExecuteAsync($"""
            insert into sessions (id, user_id, door, refresh_token_hash, created_at, last_seen_at, expires_at,
                                  absolute_expires_at, revoked_at, revoked_reason)
            values (@id, @user, 'USER', @hash, {createdAt}, {createdAt}, {expiresAt}, {absoluteExpiresAt}, {revokedAt ?? "null"},
                    {(revokedAt is null ? "null" : "'LOGOUT'")})
            """, new { id = sessionId, user, hash = "planted-" + sessionId });
        return sessionId;
    }

    private async Task<Guid> PlantChallengeAsync(Guid user, string expiresAt)
    {
        var challengeId = Guid.CreateVersion7();
        await id.Owner.ExecuteAsync($"""
            insert into mfa_challenges (id, user_id, purpose, door, token_hash, scope_type, scope_id, created_at, expires_at)
            values (@id, @user, 'VERIFY', 'USER', @hash, 'PLATFORM', null, ({expiresAt}) - interval '5 minutes', {expiresAt})
            """, new { id = challengeId, user, hash = "planted-" + challengeId });
        return challengeId;
    }

    private Task<List<Guid>> SessionsOfAsync(Guid user) =>
        id.Owner.QueryAsync<Guid>("select id from sessions where user_id = @user and refresh_token_hash like 'planted-%'", new { user });

    private Task<List<Guid>> ChallengesOfAsync(Guid user) => id.Owner.QueryAsync<Guid>("select id from mfa_challenges where user_id = @user", new { user });

    /// <summary>Every session that <c>Sessions.UsableAt</c> would accept now.</summary>
    private Task<List<Guid>> UsableSessionsAsync() => id.Owner.QueryAsync<Guid>("""
        select id from sessions
        where revoked_at is null and expires_at > now() + interval '5 minutes' and absolute_expires_at > now() + interval '5 minutes'
        """);

    private Task<List<Guid>> ExistingAsync(List<Guid> ids) =>
        id.Owner.QueryAsync<Guid>("select id from sessions where id = any(@ids)", new { ids = ids.ToArray() });

    private sealed class App(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public NpgsqlConnection Connection { get; } = connection;
        public NpgsqlTransaction Transaction { get; } = transaction;

        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    /// <summary>A transaction as the runtime role, told what <c>DbTx</c> would tell it.</summary>
    private async Task<App> AppAsync(string? scopeType)
    {
        var connection = new NpgsqlConnection(id.Owner.ConnectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync("SET LOCAL ROLE zawed_app", transaction: transaction);
        if (scopeType is not null)
        {
            await connection.ExecuteAsync("select set_config('app.scope_type', @scopeType, true)", new { scopeType }, transaction);
            if (scopeType != "PLATFORM")
            {
                await connection.ExecuteAsync("select set_config('app.scope_id', @id, true)", new { id = Guid.CreateVersion7().ToString() }, transaction);
            }
        }
        return new App(connection, transaction);
    }
}
