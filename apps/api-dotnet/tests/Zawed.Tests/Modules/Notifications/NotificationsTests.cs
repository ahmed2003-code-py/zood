using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zawed.Api.Modules.Notifications;
using Zawed.Api.Modules.Tickets.Contracts;
using Zawed.Api.Platform.Events;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Modules.Ledger;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Notifications.Notified;

namespace Zawed.Tests.Modules.Notifications;

/// <summary>
/// In-app notifications (B7 W5; Java <c>NotificationsTests</c>): each event reaches its audience (scope and roles) and
/// nobody else, a notification exists only if its cause committed, read state belongs to each user, and the database
/// refuses what the application must never do (another tenant's feed, free text, edits). The fueling and
/// station-finance causes are covered beside their own test support.
/// </summary>
public sealed class NotificationsTests(SharedApi api) : IClassFixture<SharedApi>, IAsyncLifetime
{
    private readonly IdentityTestSupport id = new(api.Factory);
    private SignedIn ops = null!;

    private Owner Owner => api.Factory.Owner;

    public async Task InitializeAsync() => ops = await id.SignInAsync(await id.SeedAsync(Role.OPS));

    public Task DisposeAsync() => Task.CompletedTask;

    // ------------------------------------------------------------------------------------------------------ tickets

    [Fact]
    public async Task ATicketOpenedByACompanyReachesOperationsAndNobodyElse()
    {
        var company = await id.SeedCompanyAsync();
        var fleet = await CompanyUserAsync(company, Role.FLEET_MANAGER);
        var accountant = await CompanyUserAsync(company, Role.ACCOUNTANT);
        var otherFleet = await CompanyUserAsync(await id.SeedCompanyAsync(), Role.FLEET_MANAGER);
        var opsManager = await id.SignInAsync(await id.SeedAsync(Role.OPS_MANAGER));
        var superAdmin = await id.SignInAsync(await id.SeedAsync(Role.SUPER_ADMIN));
        id.Limiter.Reset();
        var stationManager = await id.SignInAsync(await id.SeedAsync(Role.STATION_MANAGER));

        var ticket = await OpenCompanyTicketAsync(fleet);

        var seen = await AboutAsync(id, ops, ticket, "TICKET_OPENED");
        Assert.Equal("TICKET", seen.GetProperty("entityType").GetString());
        Assert.False(seen.GetProperty("read").GetBoolean());
        Assert.Equal("LOST_TAG", Params(seen)["ticketType"].GetString());
        Assert.Equal(company.ToString(), Params(seen)["companyId"].GetString());
        // No subject or body is copied.
        Assert.DoesNotContain("Tag lost", seen.GetRawText());
        Assert.DoesNotContain("fell off", seen.GetRawText());
        Assert.Equal(["TICKET_OPENED"], await TypesAboutAsync(id, opsManager, ticket));
        Assert.Empty(await TypesAboutAsync(id, superAdmin, ticket)); // the audience is Ops and Ops Manager
        foreach (var outsider in new[] { fleet, accountant, otherFleet, stationManager })
        {
            Assert.Equal(0, await UnreadAsync(id, outsider));
            Assert.Empty(await ListAsync(id, outsider, false));
        }
    }

    [Fact]
    public async Task OperationsAnsweringATicketTellsTheCompanyAndAnInternalNoteDoesNot()
    {
        var company = await id.SeedCompanyAsync();
        var fleet = await CompanyUserAsync(company, Role.FLEET_MANAGER);
        var accountant = await CompanyUserAsync(company, Role.ACCOUNTANT);
        var otherFleet = await CompanyUserAsync(await id.SeedCompanyAsync(), Role.FLEET_MANAGER);
        var ticket = await OpenCompanyTicketAsync(fleet);

        await ReplyAsync(ticket, "{\"body\":\"Check the tag stock first\",\"internal\":true}");
        Assert.Equal(0, await UnreadAsync(id, fleet)); // an internal note is not an answer

        await ReplyAsync(ticket, "{\"body\":\"A new tag is on its way.\"}");
        foreach (var member in new[] { fleet, accountant })
        {
            var answered = await AboutAsync(id, member, ticket, "TICKET_ANSWERED");
            Assert.Equal("LOST_TAG", Params(answered)["ticketType"].GetString());
            Assert.DoesNotContain("new tag", answered.GetRawText());
            Assert.Equal(1, await UnreadAsync(id, member));
        }
        Assert.Equal(0, await UnreadAsync(id, otherFleet));
        Assert.Equal(["TICKET_OPENED"], await TypesAboutAsync(id, ops, ticket)); // Operations wrote it
    }

    [Fact]
    public async Task AStationTicketReachesOperationsAndItsAnswerOnlyThatOperator()
    {
        var @operator = await OperatorAsync();
        var manager = await StationManagerAsync(@operator);
        var otherManager = await StationManagerAsync(await OperatorAsync());
        var fleet = await CompanyUserAsync(await id.SeedCompanyAsync(), Role.FLEET_MANAGER);

        var opened = await id.AuthedAsync("POST", "/api/v1/station/tickets", manager.AccessToken,
            "{\"type\":\"DEVICE_FAULT\",\"subject\":\"Reader dead\",\"body\":\"Pump 3 does not read tags.\"}");
        Assert.True((int)opened.StatusCode == 201, await opened.Content.ReadAsStringAsync());
        var ticket = (await opened.JsonAsync()).GetProperty("ticket").GetProperty("id").GetString()!;
        var told = Params(await AboutAsync(id, ops, ticket, "TICKET_OPENED"));
        Assert.Equal(@operator, told["operatorId"].GetString());
        Assert.Equal("DEVICE_FAULT", told["ticketType"].GetString());

        await ReplyAsync(ticket, "{\"body\":\"A technician comes tomorrow.\"}");
        Assert.Equal(["TICKET_ANSWERED"], await TypesAboutAsync(id, manager, ticket));
        Assert.Equal(0, await UnreadAsync(id, otherManager));
        Assert.Equal(0, await UnreadAsync(id, fleet));
    }

    // ------------------------------------------------------------------------------------------------- wallet money

    [Fact]
    public async Task ATopUpTellsTheApproversWhenVerifiedAndTheCompanyWhenCredited()
    {
        var company = await id.SeedCompanyAsync();
        var fleet = await CompanyUserAsync(company, Role.FLEET_MANAGER);
        var accountant = await CompanyUserAsync(company, Role.ACCOUNTANT);
        var otherFleet = await CompanyUserAsync(await id.SeedCompanyAsync(), Role.FLEET_MANAGER);
        var verifier = await id.SignInAsync(await id.SeedAsync(Role.OPS_MANAGER));
        var approver = await id.SignInAsync(await id.SeedAsync(Role.OPS_MANAGER));

        var topUp = await RequestTopUpAsync(fleet, 250_000);
        Assert.Empty(await TypesAboutAsync(id, approver, topUp)); // a claim is not yet anything to approve

        var verified = await id.AuthedAsync("POST", $"/api/v1/ops/topups/{topUp}/verify", verifier.AccessToken, "{\"receivedAmountPiastres\":240000}");
        Assert.True((int)verified.StatusCode == 200, await verified.Content.ReadAsStringAsync());
        var waiting = await AboutAsync(id, approver, topUp, "TOPUP_AWAITING_APPROVAL");
        Assert.Equal("TOPUP", waiting.GetProperty("entityType").GetString());
        Assert.Equal(company.ToString(), Params(waiting)["companyId"].GetString());
        Assert.Equal(240_000, Params(waiting)["amountPiastres"].GetInt64());
        Assert.Empty(await TypesAboutAsync(id, verifier, topUp)); // the verifier cannot approve it: not told
        Assert.Empty(await TypesAboutAsync(id, ops, topUp)); // plain Ops cannot approve
        Assert.Equal(0, await UnreadAsync(id, fleet));

        var approved = await id.AuthedAsync("POST", $"/api/v1/ops/topups/{topUp}/approve", approver.AccessToken);
        Assert.True((int)approved.StatusCode == 200, await approved.Content.ReadAsStringAsync());
        foreach (var member in new[] { fleet, accountant })
        {
            var credited = Params(await AboutAsync(id, member, topUp, "TOPUP_CREDITED"));
            Assert.Equal(240_000, credited["amountPiastres"].GetInt64());
            Assert.True(credited.ContainsKey("transactionId"));
        }
        Assert.Equal(0, await UnreadAsync(id, otherFleet));
        Assert.Equal(["TOPUP_AWAITING_APPROVAL"], await TypesAboutAsync(id, approver, topUp));

        // A repeated approval posts nothing and tells nobody again.
        await id.AuthedAsync("POST", $"/api/v1/ops/topups/{topUp}/approve", approver.AccessToken);
        Assert.Equal(["TOPUP_CREDITED"], await TypesAboutAsync(id, fleet, topUp));
    }

    [Fact]
    public async Task ARejectedTopUpRequestTellsTheCompanyWithoutTheReason()
    {
        var company = await id.SeedCompanyAsync();
        var fleet = await CompanyUserAsync(company, Role.FLEET_MANAGER);
        var topUp = await RequestTopUpAsync(fleet, 50_000);

        var rejected = await id.AuthedAsync("POST", $"/api/v1/ops/topups/{topUp}/reject", ops.AccessToken,
            "{\"reason\":\"No such transfer on the statement\"}");
        Assert.True((int)rejected.StatusCode == 200, await rejected.Content.ReadAsStringAsync());
        var told = await AboutAsync(id, fleet, topUp, "TOPUP_REJECTED");
        Assert.Equal(50_000, Params(told)["amountPiastres"].GetInt64());
        Assert.DoesNotContain("statement", told.GetRawText());
    }

    [Fact]
    public async Task AWalletAdjustmentWaitsForAnApproverAndARepeatedRequestTellsThemOnce()
    {
        var company = await id.SeedCompanyAsync();
        var fleet = await CompanyUserAsync(company, Role.FLEET_MANAGER);
        var opsManager = await id.SignInAsync(await id.SeedAsync(Role.OPS_MANAGER));
        var key = "adj-" + Guid.NewGuid();
        const string body = "{\"amountPiastres\":7000,\"reason\":\"Goodwill after a pump fault\"}";

        string? adjustment = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var asked = await new LedgerHttp(id).SendAsync("POST", $"/api/v1/ops/companies/{company}/wallet/adjustments", ops.AccessToken, body,
                new Dictionary<string, string> { ["Idempotency-Key"] = key });
            Assert.True((int)asked.StatusCode is 200 or 201, await asked.Content.ReadAsStringAsync());
            var askedId = (await asked.JsonAsync()).GetProperty("id").GetString();
            Assert.True(adjustment is null || adjustment == askedId);
            adjustment = askedId;
        }

        var waiting = Params(await AboutAsync(id, opsManager, adjustment!, "WALLET_ADJUSTMENT_AWAITING_APPROVAL"));
        Assert.Equal(company.ToString(), waiting["companyId"].GetString());
        Assert.Equal(7_000, waiting["amountPiastres"].GetInt64());
        Assert.Empty(await TypesAboutAsync(id, ops, adjustment!));
        Assert.Equal(0, await UnreadAsync(id, fleet));
    }

    // -------------------------------------------------------------------------------------------------- read state

    [Fact]
    public async Task ReadStateBelongsToEachUser()
    {
        var company = await id.SeedCompanyAsync();
        var first = await CompanyUserAsync(company, Role.FLEET_MANAGER);
        var second = await CompanyUserAsync(company, Role.FLEET_MANAGER);
        var accountant = await CompanyUserAsync(company, Role.ACCOUNTANT);
        var otherFleet = await CompanyUserAsync(await id.SeedCompanyAsync(), Role.FLEET_MANAGER);
        var ticket = await OpenCompanyTicketAsync(first);
        await ReplyAsync(ticket, "{\"body\":\"First answer\"}");
        await ReplyAsync(ticket, "{\"body\":\"Second answer\"}");

        var mine = await ListAsync(id, first, false);
        Assert.Equal(2, mine.Count);
        var newest = mine[0].GetProperty("id").GetString()!;
        var oldest = mine[1].GetProperty("id").GetString()!;
        Assert.True(string.CompareOrdinal(newest, oldest) > 0);
        Assert.Equal(2, await UnreadAsync(id, first));

        for (var i = 0; i < 2; i++) // marking twice is the same as once
        {
            var marked = await id.AuthedAsync("POST", $"/api/v1/notifications/{oldest}/read", first.AccessToken);
            Assert.True((int)marked.StatusCode == 204, await marked.Content.ReadAsStringAsync());
        }
        Assert.Equal(1, await UnreadAsync(id, first));
        Assert.Equal([newest], (await ListAsync(id, first, true)).Select(n => n.GetProperty("id").GetString()));
        var read = (await ListAsync(id, first, false))[1];
        Assert.True(read.GetProperty("read").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, read.GetProperty("readAt").ValueKind);
        Assert.Equal(2, await UnreadAsync(id, second)); // the other fleet manager has read nothing

        var all = await id.AuthedAsync("POST", "/api/v1/notifications/read-all", second.AccessToken);
        Assert.True((int)all.StatusCode == 204, await all.Content.ReadAsStringAsync());
        Assert.Equal(0, await UnreadAsync(id, second));
        Assert.Equal(1, await UnreadAsync(id, first));
        Assert.Equal(2, await UnreadAsync(id, accountant));

        // Another company's user, or Operations, cannot touch it: it does not exist for them.
        await Http.AssertErrorAsync(await id.AuthedAsync("POST", $"/api/v1/notifications/{newest}/read", otherFleet.AccessToken), 404, "NOT_FOUND");
        await Http.AssertErrorAsync(await id.AuthedAsync("POST", $"/api/v1/notifications/{newest}/read", ops.AccessToken), 404, "NOT_FOUND");
        Assert.Equal(1, await UnreadAsync(id, first));
        Assert.Equal(0, await Owner.QueryOneAsync<long>(
            "select count(*) from notification_reads where notification_id = @n::uuid and user_id in (@a, @b)",
            new { n = newest, a = otherFleet.UserId, b = ops.UserId })); // a 404 wrote nothing
    }

    [Fact]
    public async Task ABadCursorIsABadRequest()
    {
        var response = await id.AuthedAsync("GET", "/api/v1/notifications?cursor=nope", ops.AccessToken);
        await Http.AssertErrorAsync(response, 400, "INVALID_CURSOR");
    }

    // -------------------------------------------------------------------------------------- written with the cause only

    [Fact]
    public async Task ACauseThatRollsBackLeavesNoNotification()
    {
        var company = await id.SeedCompanyAsync();
        var rolledBack = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var committed = Guid.NewGuid();
        // An Ops user answers a ticket: a platform session with a user (a user-less one may only announce a low balance
        // or a hard alert, V19).
        var answeredBy = (await id.SeedAsync(Role.OPS)).Id;

        await AsPlatformAsync(answeredBy, async (events, tx) =>
        {
            await events.PublishAsync(new TicketAnswered(rolledBack, "OTHER", company, null));
            await tx.RollbackAsync();
        });
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => AsPlatformAsync(answeredBy, async (events, _) =>
        {
            await events.PublishAsync(new TicketAnswered(failed, "OTHER", company, null));
            throw new InvalidOperationException("the cause failed after announcing itself");
        }));
        Assert.Contains("the cause failed", thrown.Message);
        await AsPlatformAsync(answeredBy, async (events, tx) =>
        {
            await events.PublishAsync(new TicketAnswered(committed, "OTHER", company, null));
            await tx.CommitAsync();
        });

        Assert.Equal(0, await NotificationsAboutAsync(rolledBack));
        Assert.Equal(0, await NotificationsAboutAsync(failed));
        Assert.Equal(1, await NotificationsAboutAsync(committed));
    }

    [Fact]
    public async Task AnEventOutsideATransactionIsRefusedRatherThanWrittenUnscoped()
    {
        var ticket = Guid.NewGuid();
        var company = await id.SeedCompanyAsync();
        await using var scope = api.Factory.Services.CreateAsyncScope();
        var events = scope.ServiceProvider.GetRequiredService<IDomainEvents>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => events.PublishAsync(new TicketAnswered(ticket, "OTHER", company, null)));
        Assert.Equal(0, await NotificationsAboutAsync(ticket));
    }

    // ------------------------------------------------------------------------------------- the database's own rules

    [Fact]
    public async Task RowLevelSecurityShowsEachUserOnlyTheirScopeAndRole()
    {
        var companyA = Scope.Company(await id.SeedCompanyAsync());
        var companyB = Scope.Company(await id.SeedCompanyAsync());
        var fleetA = await id.SeedAsync(Role.FLEET_MANAGER, companyA, false);
        var accountantA = await id.SeedAsync(Role.ACCOUNTANT, companyA, false);
        var fleetB = await id.SeedAsync(Role.FLEET_MANAGER, companyB, false);
        var opsUser = await id.SeedAsync(Role.OPS);
        var opsManager = await id.SeedAsync(Role.OPS_MANAGER);
        var forFleetA = await PlantAsync(companyA, "{FLEET_MANAGER}", null);
        var forCompanyB = await PlantAsync(companyB, "{FLEET_MANAGER,ACCOUNTANT}", null);
        var forApprovers = await PlantAsync(Scope.Platform, "{OPS_MANAGER}", null);
        var causedByFleetA = await PlantAsync(companyA, "{FLEET_MANAGER}", fleetA.Id);
        Guid[] planted = [forFleetA, forCompanyB, forApprovers, causedByFleetA];

        Assert.Equal([forFleetA], await VisibleAsync(companyA, fleetA.Id, planted));
        Assert.Empty(await VisibleAsync(companyA, accountantA.Id, planted)); // not addressed to accountants
        Assert.Equal([forCompanyB], await VisibleAsync(companyB, fleetB.Id, planted));
        Assert.Empty(await VisibleAsync(Scope.Platform, opsUser.Id, planted)); // the platform does not read company feeds
        Assert.Equal([forApprovers], await VisibleAsync(Scope.Platform, opsManager.Id, planted));
        Assert.Empty(await VisibleAsync(companyA, null, planted)); // no user, no role, nothing
        // A user's role comes from their row: another company's user id in this scope has no role here.
        Assert.Empty(await VisibleAsync(companyA, fleetB.Id, planted));
    }

    [Fact]
    public async Task TheApplicationCannotWriteIntoAnotherTenantsFeedNorChangeOne()
    {
        var companyA = Scope.Company(await id.SeedCompanyAsync());
        var companyB = Scope.Company(await id.SeedCompanyAsync());
        var fleetA = await id.SeedAsync(Role.FLEET_MANAGER, companyA, false);
        var accountantA = await id.SeedAsync(Role.ACCOUNTANT, companyA, false);
        var forCompanyB = await PlantAsync(companyB, "{FLEET_MANAGER}", null);
        var forFleetA = await PlantAsync(companyA, "{FLEET_MANAGER}", null);

        // Each refusal aborts its transaction, so each gets its own.
        Assert.Contains("row-level security", (await RefusedAsAppAsync(Owner, companyA, fleetA.Id, (c, tx) => InsertAsync(c, tx, companyB, "app_user_id()"))).Message);
        // The actor is the transaction's user, never another.
        Assert.Contains("row-level security",
            (await RefusedAsAppAsync(Owner, companyA, fleetA.Id, (c, tx) => InsertAsync(c, tx, Scope.Platform, $"'{accountantA.Id}'"))).Message);
        await AsAppAsync(Owner, companyA, fleetA.Id, async (c, tx) =>
        {
            await InsertAsync(c, tx, Scope.Platform, "app_user_id()"); // a ticket it opened: Operations is told
            await InsertAsync(c, tx, companyA, "app_user_id()");
            return 0;
        });
        // A user-less company transaction writes nothing.
        Assert.Contains("row-level security", (await RefusedAsAppAsync(Owner, companyA, null, (c, tx) => InsertAsync(c, tx, companyA, "null"))).Message);
        // No scope, nothing.
        Assert.Contains("row-level security", (await RefusedAsAppAsync(Owner, null, null, (c, tx) => InsertAsync(c, tx, Scope.Platform, "null"))).Message);
        foreach (var change in new[]
                 {
                     "update notifications set params = '{}'", "delete from notifications", "update notification_reads set read_at = now()",
                     "delete from notification_reads",
                 })
        {
            Assert.Contains("permission denied", (await RefusedAsAppAsync(Owner, Scope.Platform, null, (c, tx) => ExecuteAsync(c, tx, change))).Message);
        }
        // Someone else's read state.
        Assert.Contains("row-level security",
            (await RefusedAsAppAsync(Owner, companyA, fleetA.Id, (c, tx) => MarkReadAsync(c, tx, accountantA.Id, forFleetA))).Message);
        // A notification it cannot see.
        Assert.Contains("row-level security",
            (await RefusedAsAppAsync(Owner, companyA, fleetA.Id, (c, tx) => MarkReadAsync(c, tx, fleetA.Id, forCompanyB))).Message);
        await AsAppAsync(Owner, companyA, fleetA.Id, (c, tx) => MarkReadAsync(c, tx, fleetA.Id, forFleetA));
    }

    [Fact]
    public async Task ANotificationCarriesIdsNumbersAndCodesOnly()
    {
        var company = await id.SeedCompanyAsync();
        // TICKET_ANSWERED carries one key, ticketType; the value rules are tried on that key, and any other key is refused
        // for this type whatever its value (B7 W5 review, L2).
        foreach (var bad in new[]
                 {
                     "{\"reason\":\"Customer called twice\"}", "{\"plate\":\"ABC 123\"}", "{\"ticketType\":\"Lost tag\"}", "{\"ticketType\":15.5}",
                     "{\"ticketType\":\"lost_tag\"}", "{\"companyName\":\"Acme\"}", "{\"Bad Key\":1}", "[1,2]", "{\"ticketType\":{\"a\":1}}",
                     "{\"ticketType\":true}", "{\"ticketType\":\"LOST_TAG\",\"amountPiastres\":1500}",
                 })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => PlantSqlAsync("COMPANY", company, "{FLEET_MANAGER}", bad, null));
            Assert.Contains("check constraint", refused.Message);
        }
        await PlantSqlAsync("COMPANY", company, "{FLEET_MANAGER}", "{\"ticketType\":\"LOST_TAG\"}", null);
        // A company role in a platform audience.
        Assert.Contains("check constraint",
            (await Assert.ThrowsAsync<PostgresException>(() => PlantSqlAsync("PLATFORM", null, "{FLEET_MANAGER}", "{}", null))).Message);
        // A company audience without its company.
        Assert.Contains("check constraint",
            (await Assert.ThrowsAsync<PostgresException>(() => PlantSqlAsync("COMPANY", null, "{FLEET_MANAGER}", "{}", null))).Message);
    }

    // --------------------------------------------------------------------------------------------------- retention

    [Fact]
    public async Task NotificationsOlderThanTheRetentionArePurgedWithTheirReadState()
    {
        var company = Scope.Company(await id.SeedCompanyAsync());
        var fleet = await id.SeedAsync(Role.FLEET_MANAGER, company, false);
        var old = await PlantAsync(company, "{FLEET_MANAGER}", null);
        var recent = await PlantAsync(company, "{FLEET_MANAGER}", null);
        await Owner.ExecuteAsync("update notifications set created_at = now() - interval '91 days' where id = @old", new { old });
        await Owner.ExecuteAsync("update notifications set created_at = now() - interval '10 days' where id = @recent", new { recent });
        await Owner.ExecuteAsync("insert into notification_reads (user_id, notification_id, read_at) values (@u, @old, now())", new { u = fleet.Id, old });

        Assert.True(await api.Factory.Services.GetRequiredService<NotificationRetention>().PurgeAsync() >= 1);
        Assert.Equal(0, await Owner.QueryOneAsync<long>("select count(*) from notifications where id = @old", new { old }));
        Assert.Equal(0, await Owner.QueryOneAsync<long>("select count(*) from notification_reads where notification_id = @old", new { old }));
        Assert.Equal(1, await Owner.QueryOneAsync<long>("select count(*) from notifications where id = @recent", new { recent }));
    }

    [Fact]
    public async Task ThePurgeRefusesTheLastThirtyDays()
    {
        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            Owner.QueryOneAsync<int>("select purge_notifications(now() - interval '29 days', 10)"));
        Assert.Contains("30-day minimum retention", refused.Message);
    }

    /// <summary>The job's run is recorded under the Java name (not a Java test: the .NET job wrapper's run).</summary>
    [Fact]
    public async Task TheRetentionJobRunsAsARecordedJob()
    {
        var result = await api.Factory.Services.GetRequiredService<NotificationRetention>().RunNowAsync();
        Assert.Equal(Api.Platform.Jobs.JobOutcome.SUCCESS, result.Outcome);
        Assert.True(await Owner.QueryOneAsync<long>("select count(*) from job_runs where job_name = 'notification-retention'") >= 1);
    }

    // ----------------------------------------------------------------------------------------------------- helpers

    private async Task<SignedIn> StationManagerAsync(string @operator) =>
        await id.SignInAsync(await id.SeedAsync(Role.STATION_MANAGER, Scope.Operator(Guid.Parse(@operator)), false));

    private async Task<string> OpenCompanyTicketAsync(SignedIn fleet)
    {
        var opened = await id.AuthedAsync("POST", "/api/v1/company/tickets", fleet.AccessToken,
            "{\"type\":\"LOST_TAG\",\"subject\":\"Tag lost on the road\",\"body\":\"The tag of car 12 fell off.\"}");
        Assert.True((int)opened.StatusCode == 201, await opened.Content.ReadAsStringAsync());
        return (await opened.JsonAsync()).GetProperty("ticket").GetProperty("id").GetString()!;
    }

    private async Task ReplyAsync(string ticket, string body)
    {
        var answer = await id.AuthedAsync("POST", $"/api/v1/ops/tickets/{ticket}/messages", ops.AccessToken, body);
        Assert.True((int)answer.StatusCode == 200, await answer.Content.ReadAsStringAsync());
    }

    private async Task<string> OperatorAsync()
    {
        var created = await id.AuthedAsync("POST", "/api/v1/ops/operators", ops.AccessToken,
            $"{{\"legalName\":\"Op {Guid.NewGuid()}\",\"nameAr\":\"مشغل\",\"nameEn\":\"Operator\"}}");
        Assert.True((int)created.StatusCode == 201, await created.Content.ReadAsStringAsync());
        return (await created.JsonAsync()).GetProperty("id").GetString()!;
    }

    /// <summary>
    /// A transaction as the platform with this user (Java <c>DbScope.runAs</c> plus <c>TransactionTemplate</c>): the
    /// events published in it reach the handlers on it; <paramref name="work"/> commits or rolls it back, and an exception
    /// rolls it back.
    /// </summary>
    private async Task AsPlatformAsync(Guid userId, Func<IDomainEvents, DbTx, Task> work)
    {
        await using var scope = api.Factory.Services.CreateAsyncScope();
        var session = scope.ServiceProvider.GetRequiredService<DbSession>();
        await using var tx = await scope.ServiceProvider.GetRequiredService<ITransactions>()
            .BeginAsync(DbScopeContext.Escalate(Scope.Platform, userId));
        session.Attach(tx);
        try
        {
            await work(scope.ServiceProvider.GetRequiredService<IDomainEvents>(), tx);
        }
        finally
        {
            session.Detach();
        }
    }

    private async Task<long> NotificationsAboutAsync(Guid entity) =>
        await Owner.QueryOneAsync<long>("select count(*) from notifications where entity_id = @entity", new { entity });

    private async Task<SignedIn> CompanyUserAsync(Guid company, Role role) =>
        await id.SignInAsync(await id.SeedAsync(role, Scope.Company(company), false));

    private async Task<string> RequestTopUpAsync(SignedIn member, long piastres)
    {
        var requested = await id.AuthedAsync("POST", "/api/v1/company/topups", member.AccessToken,
            $"{{\"amountPiastres\":{piastres},\"bankReference\":\"FT-{Guid.NewGuid()}\",\"transferDate\":\"{LedgerHttp.Today:yyyy-MM-dd}\"}}");
        Assert.True((int)requested.StatusCode == 201, await requested.Content.ReadAsStringAsync());
        return (await requested.JsonAsync()).GetProperty("id").GetString()!;
    }

    private const string PlantSql = """
        insert into notifications (id, type, audience_scope_type, audience_scope_id, audience_roles, entity_type,
                                   entity_id, params, actor_id, created_at)
        values (@id, 'TICKET_ANSWERED', @scopeType, @scopeId, @roles::text[], 'TICKET', gen_random_uuid(), @params::jsonb, @actor, now())
        """;

    private Task<int> PlantSqlAsync(string scopeType, Guid? scopeId, string roles, string @params, Guid? actor) =>
        Owner.ExecuteAsync(PlantSql, new { id = Guid.NewGuid(), scopeType, scopeId, roles, @params, actor });

    /// <summary>A notification written as the owner, bypassing row-level security.</summary>
    private async Task<Guid> PlantAsync(Scope audience, string roles, Guid? actor)
    {
        var planted = Guid.NewGuid();
        await Owner.ExecuteAsync(PlantSql, new { id = planted, scopeType = audience.Type.ToString(), scopeId = audience.Id, roles, @params = "{}", actor });
        return planted;
    }

    private async Task<List<Guid>> VisibleAsync(Scope scope, Guid? userId, Guid[] among)
    {
        var ids = await AsAppAsync(Owner, scope, userId, async (c, tx) =>
            (await c.QueryAsync<Guid>("select id from notifications where id = any(@among) order by id", new { among }, tx)).ToList());
        return ids.OrderBy(i => Array.IndexOf(among, i)).ToList();
    }

    private static Task<int> InsertAsync(NpgsqlConnection c, NpgsqlTransaction tx, Scope audience, string actorExpression) =>
        ExecuteAsync(c, tx, """
            insert into notifications (id, type, audience_scope_type, audience_scope_id, audience_roles, entity_type,
                                       entity_id, params, actor_id, created_at)
            values (gen_random_uuid(), 'TICKET_OPENED', @scopeType, @scopeId, @roles::text[], 'TICKET', gen_random_uuid(), '{}',
            """ + actorExpression + ", now())",
            ("scopeType", audience.Type.ToString()), ("scopeId", audience.Id), ("roles", audience.IsPlatform ? "{OPS}" : "{FLEET_MANAGER}"));

    private static Task<int> MarkReadAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, Guid notification) =>
        ExecuteAsync(c, tx, "insert into notification_reads (user_id, notification_id, read_at) values (@u, @n, now())", ("u", userId), ("n", notification));
}
