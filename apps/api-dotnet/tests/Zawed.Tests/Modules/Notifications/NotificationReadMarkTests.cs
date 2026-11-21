using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Zawed.Api.Modules.Notifications;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Modules.Ledger;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Notifications.Notified;

namespace Zawed.Tests.Modules.Notifications;

/// <summary>
/// Read state by a per-user mark (B7 W5 review, M2) and the feed's keyset cursor (L4; Java <c>NotificationReadMarkTests</c>):
/// "mark all read" writes one mark, not a row per notification; the unread count ranges over what is newer than the mark
/// through the audience index; a notification still being committed when the user marks all stays unread; concurrent
/// clicks only move the mark forward; and paging by <c>(created_at, id)</c> neither skips nor repeats rows that share a
/// time.
/// </summary>
public sealed class NotificationReadMarkTests(SharedApi api) : IClassFixture<SharedApi>
{
    private readonly IdentityTestSupport id = new(api.Factory);

    private Owner Owner => api.Factory.Owner;

    // ------------------------------------------------------------------------------------------------------------ M2

    [Fact]
    public async Task MarkingAllReadWritesAMarkNotARowPerNotification()
    {
        var company = Scope.Company(await id.SeedCompanyAsync());
        var fleet = await id.SignInAsync(await id.SeedAsync(Role.FLEET_MANAGER, company, false));
        for (var i = 0; i < 30; i++)
        {
            await PlantAsync(company, DateTimeOffset.UtcNow.AddHours(-2).AddSeconds(i));
        }
        var recent = await PlantAsync(company, DateTimeOffset.UtcNow);
        Assert.Equal(31, await UnreadAsync(id, fleet));

        Assert.Equal(204, (int)(await id.AuthedAsync("POST", "/api/v1/notifications/read-all", fleet.AccessToken)).StatusCode);

        Assert.Equal(0, await UnreadAsync(id, fleet));
        Assert.Empty(await ListAsync(id, fleet, true));
        var all = await ListAsync(id, fleet, false);
        Assert.Equal(31, all.Count);
        Assert.All(all, n =>
        {
            Assert.True(n.GetProperty("read").GetBoolean());
            Assert.NotEqual(JsonValueKind.Null, n.GetProperty("readAt").ValueKind);
        });
        Assert.Equal(1, await Owner.QueryOneAsync<long>("select count(*) from notification_read_marks where user_id = @u", new { u = fleet.UserId }));
        // Only what the grace window holds is marked row by row: here the one recent notice.
        Assert.Equal([recent], await Owner.QueryAsync<Guid>("select notification_id from notification_reads where user_id = @u",
            new { u = fleet.UserId }));

        var later = await PlantAsync(company, DateTimeOffset.UtcNow);
        Assert.Equal(1, await UnreadAsync(id, fleet));
        Assert.Equal([later.ToString()], (await ListAsync(id, fleet, true)).Select(n => n.GetProperty("id").GetString()));
        Assert.Equal(204, (int)(await id.AuthedAsync("POST", $"/api/v1/notifications/{later}/read", fleet.AccessToken)).StatusCode);
        Assert.Equal(0, await UnreadAsync(id, fleet));
    }

    /// <summary>
    /// The race the grace window is for: a notification inserted before the click and committed after it was never seen,
    /// so it must stay unread. A mark at the moment of the click would cover it.
    /// </summary>
    [Fact]
    public async Task ANotificationCommittedWhileTheUserMarksAllReadStaysUnread()
    {
        var company = Scope.Company(await id.SeedCompanyAsync());
        var fleet = await id.SignInAsync(await id.SeedAsync(Role.FLEET_MANAGER, company, false));
        var answeredBy = (await id.SeedAsync(Role.OPS)).Id;
        await PlantAsync(company, DateTimeOffset.UtcNow.AddHours(-1));
        var late = Guid.NewGuid();

        var (open, tx) = await RawSql.OpenAsync(Owner, asApp: true, "PLATFORM", null, answeredBy);
        await using (open)
        await using (tx)
        {
            // Inserted (and stamped) now, committed only after the click below.
            await ExecuteAsync(open, tx, """
                insert into notifications (id, type, audience_scope_type, audience_scope_id, audience_roles,
                                           entity_type, entity_id, params, actor_id, created_at)
                values (@id, 'TICKET_ANSWERED', 'COMPANY', @company, '{FLEET_MANAGER}', 'TICKET', gen_random_uuid(), '{}',
                        app_user_id(), clock_timestamp())
                """, ("id", late), ("company", company.Id));
            var all = await id.AuthedAsync("POST", "/api/v1/notifications/read-all", fleet.AccessToken);
            Assert.True((int)all.StatusCode == 204, await all.Content.ReadAsStringAsync());
            Assert.Equal(0, await UnreadAsync(id, fleet));
            await tx.CommitAsync();
        }

        Assert.Equal(1, await UnreadAsync(id, fleet));
        Assert.Equal([late.ToString()], (await ListAsync(id, fleet, true)).Select(n => n.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task ConcurrentMarkAllClicksOnlyMoveTheMarkForward()
    {
        var company = Scope.Company(await id.SeedCompanyAsync());
        var fleet = await id.SignInAsync(await id.SeedAsync(Role.FLEET_MANAGER, company, false));
        for (var i = 0; i < 5; i++)
        {
            await PlantAsync(company, DateTimeOffset.UtcNow.AddSeconds(-i));
        }
        var statuses = await Concurrently.RunAsync(8,
            async _ => (int)(await id.AuthedAsync("POST", "/api/v1/notifications/read-all", fleet.AccessToken)).StatusCode);
        Assert.All(statuses, s => Assert.Equal(204, s));
        Assert.Equal(0, await UnreadAsync(id, fleet));
        Assert.Equal(1, await Owner.QueryOneAsync<long>("select count(*) from notification_read_marks where user_id = @u", new { u = fleet.UserId }));
        // One row per notification, however many clicks.
        Assert.Equal(5, await Owner.QueryOneAsync<long>("select count(*) from notification_reads where user_id = @u", new { u = fleet.UserId }));

        var mark = await Owner.QueryOneAsync<DateTime>("select read_up_to from notification_read_marks where user_id = @u", new { u = fleet.UserId });
        Assert.Equal(204, (int)(await id.AuthedAsync("POST", "/api/v1/notifications/read-all", fleet.AccessToken)).StatusCode);
        var next = await Owner.QueryOneAsync<DateTime>("select read_up_to from notification_read_marks where user_id = @u", new { u = fleet.UserId });
        Assert.True(next >= mark);
    }

    [Fact]
    public async Task AMarkIsItsOwnUsersAndNeverMovesBack()
    {
        var company = Scope.Company(await id.SeedCompanyAsync());
        var fleet = await id.SeedAsync(Role.FLEET_MANAGER, company, false);
        var accountant = await id.SeedAsync(Role.ACCOUNTANT, company, false);
        await Owner.ExecuteAsync("insert into notification_read_marks (user_id, read_up_to, marked_at) values (@u, now(), now())",
            new { u = accountant.Id });

        // Someone else's mark is invisible.
        Assert.Equal(0, await AsAppAsync(Owner, company, fleet.Id,
            (c, tx) => c.QuerySingleAsync<long>("select count(*) from notification_read_marks", transaction: tx)));
        var foreign = await RefusedAsAppAsync(Owner, company, fleet.Id, (c, tx) => ExecuteAsync(c, tx,
            "insert into notification_read_marks (user_id, read_up_to, marked_at) values (@u, now(), now())", ("u", accountant.Id)));
        Assert.Contains("row-level security", foreign.Message);
        var back = await RefusedAsAppAsync(Owner, company, accountant.Id, (c, tx) => ExecuteAsync(c, tx,
            "update notification_read_marks set read_up_to = read_up_to - interval '1 day'"));
        Assert.Contains("only moves forward", back.Message);
        var delete = await RefusedAsAppAsync(Owner, company, accountant.Id, (c, tx) => ExecuteAsync(c, tx, "delete from notification_read_marks"));
        Assert.Contains("permission denied", delete.Message);
        // Another user's mark cannot be moved either.
        Assert.Equal(0, await AsAppAsync(Owner, company, fleet.Id, (c, tx) => ExecuteAsync(c, tx,
            "update notification_read_marks set read_up_to = read_up_to + interval '1 day'")));
    }

    /// <summary>
    /// The unread count is an index range from the mark onward, so Operations' count reads what arrived since they last
    /// marked all read, not the platform's whole feed; the feed's page is a row comparison on the audience index. Checked
    /// on the application's own statements, as Ops, with row-level security on. (A bound that stayed a filter would scan
    /// the feed: the first version of this fix, with the mark inline under coalesce, did exactly that under row-level
    /// security.)
    /// </summary>
    [Fact]
    public async Task TheUnreadCountAndTheFeedAreRangesOnTheAudienceIndex()
    {
        var opsUser = await id.SeedAsync(Role.OPS);
        var ops = new ZawedPrincipal(opsUser.Id, Guid.NewGuid(), Role.OPS, Scope.Platform, false);

        // Either index bounds it by the mark: the audience one, or the purge's (created_at) one when the planner judges
        // the platform audience to be most of the table.
        var unreadPlan = await PlanAsync(ops, Api.Modules.Notifications.Notifications.UnreadSql(ops),
            ("cap", 100), ("mark", DateTimeOffset.UtcNow.AddHours(-1)));
        Assert.Contains(unreadPlan, l => l.Contains("notifications_audience_idx") || l.Contains("notifications_created_at_idx"));
        Assert.Contains(unreadPlan, l => l.Contains("Index Cond") && l.Contains("created_at >"));

        var pagePlan = await PlanAsync(ops, Api.Modules.Notifications.Notifications.FeedSql(ops, false, true),
            ("limit", 26), ("cursorAt", DateTimeOffset.UtcNow), ("cursorId", Guid.NewGuid()));
        Assert.Contains(pagePlan, l => l.Contains("notifications_audience_idx"));
        Assert.Contains(pagePlan, l => l.Contains("Index Cond") && l.Contains("ROW(") && l.Contains("created_at"));

        var unreadPagePlan = await PlanAsync(ops, Api.Modules.Notifications.Notifications.FeedSql(ops, true, false),
            ("limit", 26), ("mark", DateTimeOffset.UtcNow.AddHours(-1)));
        Assert.Contains(unreadPagePlan, l => l.Contains("Index Cond") && l.Contains("created_at"));
    }

    // ------------------------------------------------------------------------------------------------------------ L4

    [Fact]
    public async Task PagingByTimeAndIdNeitherSkipsNorRepeatsRowsThatShareATime()
    {
        var company = Scope.Company(await id.SeedCompanyAsync());
        var fleet = await id.SignInAsync(await id.SeedAsync(Role.FLEET_MANAGER, company, false));
        var now = DateTimeOffset.UtcNow.AddHours(-1);
        var tie = new DateTimeOffset(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero); // whole microseconds
        var planted = new List<(Guid Id, DateTimeOffset CreatedAt)>();
        for (var i = 0; i < 7; i++)
        {
            // Random ids, not time-ordered ones, so id order says nothing about time.
            planted.Add((await PlantAsync(company, tie), tie));
        }
        planted.Add((await PlantAsync(company, tie.AddSeconds(1)), tie.AddSeconds(1)));
        planted.Add((await PlantAsync(company, tie.AddSeconds(-1)), tie.AddSeconds(-1)));
        // PostgreSQL orders uuids byte by byte, which is the order of their lower-case text (not Guid.CompareTo's).
        var expected = planted.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id.ToString(), StringComparer.Ordinal)
            .Select(p => p.Id.ToString()).ToList();

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await id.AuthedAsync("GET", "/api/v1/notifications?limit=2" + (cursor is null ? "" : "&cursor=" + cursor), fleet.AccessToken);
            var body = await page.Content.ReadAsStringAsync();
            Assert.True((int)page.StatusCode == 200, body);
            var root = JsonDocument.Parse(body).RootElement;
            seen.AddRange(root.GetProperty("items").EnumerateArray().Select(n => n.GetProperty("id").GetString()!));
            cursor = root.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null && pages < 20);

        Assert.Equal(expected, seen);
        Assert.Equal(5, pages);
    }

    [Fact]
    public async Task ACursorThatIsNotAKeysetIsABadRequest()
    {
        var fleet = await id.SignInAsync(await id.SeedAsync(Role.FLEET_MANAGER, Scope.Company(await id.SeedCompanyAsync()), false));
        foreach (var bad in new[]
                 {
                     Guid.NewGuid().ToString(), "12.nope", "x." + Guid.NewGuid(), "99999999999999999999." + Guid.NewGuid(),
                     "-9000000000000000000." + Guid.NewGuid(), "-1." + Guid.NewGuid(), "9000000000000000000." + Guid.NewGuid(),
                 })
        {
            var response = await id.AuthedAsync("GET", "/api/v1/notifications?cursor=" + bad, fleet.AccessToken);
            await Http.AssertErrorAsync(response, 400, "INVALID_CURSOR");
        }
    }

    /// <summary>
    /// Java's <c>UUID.fromString</c> also takes five dash-separated hex groups that are not 8-4-4-4-12 (each masked to its
    /// field): such a cursor is a valid keyset there, and so here. Not in the Java class; it pins the literal port.
    /// </summary>
    [Fact]
    public void ACursorIdIsReadAsJavaReadsAUuid()
    {
        Assert.Equal(new Keyset(DateTimeOffset.UnixEpoch.AddTicks(10), Guid.Parse("00000001-0002-0003-0004-000000000005")),
            Keyset.Parse("1.1-2-3-4-0000000000000000000000000005"));
        Assert.Equal(Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"), Keyset.Parse("0.01234567-89AB-CDEF-0123-456789ABCDEF")!.Id);
        foreach (var bad in new[] { "1.1-2-3-4-5-00000000000000000000000000", "1.------------------------------------",
                     "1.123456789abcdef01-2-3-4-000000000000", "1.ffffffffffffffff-2-3-4-0000000000000" })
        {
            Assert.Equal("INVALID_CURSOR", Assert.Throws<Api.Platform.Web.ApiException>(() => Keyset.Parse(bad)).Code);
        }
    }

    // ------------------------------------------------------------------------------------------------------- helpers

    /// <summary>The statement's plan as <paramref name="me"/>, in their scope, with sequential scans off (tables are small here).</summary>
    private async Task<List<string>> PlanAsync(ZawedPrincipal me, string sql, params (string Name, object Value)[] args)
    {
        var transactions = api.Factory.Services.GetRequiredService<ITransactions>();
        return await transactions.RunAsync(DbScopeContext.Escalate(me.Scope, me.UserId), async tx =>
        {
            await tx.ExecuteAsync("set local enable_seqscan = off");
            var bound = Api.Modules.Notifications.Notifications.Audience(me);
            foreach (var (name, value) in args)
            {
                bound.Add(name, value);
            }
            return (await tx.QueryAsync<string>("explain " + sql, bound)).ToList();
        });
    }

    /// <summary>A notification for the company's fleet managers, written as the owner, at a given time.</summary>
    private async Task<Guid> PlantAsync(Scope company, DateTimeOffset createdAt)
    {
        var planted = Guid.NewGuid();
        await Owner.ExecuteAsync("""
            insert into notifications (id, type, audience_scope_type, audience_scope_id, audience_roles, entity_type,
                                       entity_id, params, actor_id, created_at)
            values (@planted, 'TICKET_ANSWERED', 'COMPANY', @company, '{FLEET_MANAGER}', 'TICKET', gen_random_uuid(), '{}', null, @createdAt)
            """, new { planted, company = company.Id, createdAt });
        return planted;
    }
}
