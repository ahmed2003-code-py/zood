using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zawed.Api.Modules.Notifications;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Modules.Ledger;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Notifications.Notified;

namespace Zawed.Tests.Modules.Notifications;

/// <summary>
/// What may be written, by whom, and what a refused notification does to its cause (B7 W5 review; Java
/// <c>NotificationWriteRulesTests</c>): a user-less platform session announces only a low balance or a hard alert (L1);
/// each type carries only its own parameter keys, in C# and in the database alike (L2); and a refused notification never
/// takes a top-up decision or a ticket down with it (M1, the money paths beside the fueling one, which
/// <c>NotificationFailureTests</c> covers).
/// </summary>
public sealed class NotificationWriteRulesTests(SharedApi api) : IClassFixture<SharedApi>
{
    private readonly IdentityTestSupport id = new(api.Factory);

    private Owner Owner => api.Factory.Owner;

    private NotificationMetrics Metrics => api.Factory.Services.GetRequiredService<NotificationMetrics>();

    // ------------------------------------------------------------------------------------------------------------ L1

    [Fact]
    public async Task AUserlessPlatformSessionAnnouncesOnlyALowBalanceOrAHardAlert()
    {
        var written = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var type in Enum.GetValues<NotificationType>())
        {
            await AsAppAsync(Owner, Scope.Platform, null, async (c, tx) =>
            {
                try
                {
                    await InsertAsync(c, tx, type, "{}");
                    written.Add(type.ToString());
                }
                catch (PostgresException ex)
                {
                    Assert.True(ex.Message.Contains("row-level security", StringComparison.Ordinal), $"{type}: {ex.Message}");
                }
                return 0;
            });
        }
        Assert.Equal(["HARD_ALERT_RAISED", "WALLET_LOW_BALANCE"], written);

        // A platform session with a user (Operations at work) may announce any type.
        var opsUser = (await id.SeedAsync(Role.OPS)).Id;
        foreach (var type in Enum.GetValues<NotificationType>())
        {
            await AsAppAsync(Owner, Scope.Platform, opsUser, (c, tx) => InsertAsync(c, tx, type, "{}"));
        }
    }

    // ------------------------------------------------------------------------------------------------------------ L2

    [Fact]
    public async Task EachTypeCarriesOnlyItsOwnKeysAndTheDatabaseAgreesWithTheCode()
    {
        var company = await id.SeedCompanyAsync();
        foreach (var type in Enum.GetValues<NotificationType>())
        {
            var keys = await Owner.QueryOneAsync<string[]>("select notification_param_keys(@type)", new { type = type.ToString() });
            Assert.True(keys!.ToHashSet().SetEquals(type.Keys()), $"database keys of {type}");

            var full = type.Keys().ToDictionary(k => k, Sample);
            await PlantAsync(type, company, JsonSerializer.Serialize(full)); // every key at once is fine
            await PlantAsync(type, company, "{}"); // and so is none: a key that does not apply is left out

            var extra = new Dictionary<string, object>(full) { ["unexpectedKey"] = 1 };
            var refused = await Assert.ThrowsAsync<PostgresException>(() => PlantAsync(type, company, JsonSerializer.Serialize(extra)));
            Assert.True(refused.Message.Contains("notifications_params", StringComparison.Ordinal), $"{type} with another key");
        }
        // The value rules still hold on an allowed key.
        foreach (var bad in new[]
                 {
                     "{\"amountPiastres\":\"1500.00\"}", "{\"amountPiastres\":15.5}", "{\"amountPiastres\":true}", "{\"stationId\":\"Station 4\"}",
                     "{\"stationId\":{\"id\":1}}",
                 })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => PlantAsync(NotificationType.PAYOUT_POSTED, company, bad));
            Assert.True(refused.Message.Contains("notifications_params", StringComparison.Ordinal), bad);
        }
    }

    [Fact]
    public async Task AKeyTheTypeDoesNotCarryIsRefusedAtWriteAndCountedAndTheTransactionGoesOn()
    {
        var company = await id.SeedCompanyAsync();
        var bad = Guid.NewGuid();
        var good = Guid.NewGuid();
        var opsUser = (await id.SeedAsync(Role.OPS)).Id;
        var before = Metrics.Dropped(NotificationType.TOPUP_CREDITED);
        var audience = Audience.Of(Scope.Company(company), Role.FLEET_MANAGER);

        await InScopeAsync(DbScopeContext.Escalate(Scope.Platform, opsUser), async notifications =>
        {
            await notifications.SendAsync(NotificationType.TOPUP_CREDITED, () => new Notice(audience, NotificationEntityType.TOPUP, bad,
                NotificationParams.Of().Number("amountPiastres", 100).Code("ticketType", "OTHER")));
            // Still in a healthy transaction: an aborted one would refuse this statement.
            await notifications.SendAsync(NotificationType.TOPUP_CREDITED, () => new Notice(audience, NotificationEntityType.TOPUP, good,
                NotificationParams.Of().Number("amountPiastres", 100)));
        });

        Assert.Equal(0, await Owner.QueryOneAsync<long>("select count(*) from notifications where entity_id = @bad", new { bad }));
        Assert.Equal(1, await Owner.QueryOneAsync<long>("select count(*) from notifications where entity_id = @good", new { good }));
        Assert.Equal(1, Metrics.Dropped(NotificationType.TOPUP_CREDITED) - before);
    }

    /// <summary>
    /// A notice the database refuses (here a trigger, as in the M1 tests) is rolled back to its savepoint while the
    /// cause's own earlier and later statements commit. Not a Java test: it pins the savepoint itself, beside the
    /// Java-shaped M1 tests over HTTP.
    /// </summary>
    [Fact]
    public async Task ARefusedNoticeRollsBackToItsSavepointAndTheCauseCommits()
    {
        var company = await id.SeedCompanyAsync();
        var opsUser = (await id.SeedAsync(Role.OPS)).Id;
        var refused = Guid.NewGuid();
        var kept = Guid.NewGuid();
        var before = Metrics.Dropped(NotificationType.TOPUP_REJECTED);
        var trigger = await RefuseNotificationsAboutAsync(company);
        try
        {
            await InScopeAsync(DbScopeContext.Escalate(Scope.Platform, opsUser), async notifications =>
            {
                // The cause's own write before the notice, and one after it, both on the same transaction.
                await PlantInTxAsync(kept);
                await notifications.SendAsync(NotificationType.TOPUP_REJECTED, () => new Notice(
                    Audience.Of(Scope.Company(company), Role.FLEET_MANAGER), NotificationEntityType.TOPUP, refused,
                    NotificationParams.Of().Number("amountPiastres", 1)));
                await PlantInTxAsync(Guid.NewGuid(), kept);
            });
        }
        finally
        {
            await DropAsync(trigger);
        }
        Assert.Equal(0, await Owner.QueryOneAsync<long>("select count(*) from notifications where entity_id = @refused", new { refused }));
        Assert.Equal(2, await Owner.QueryOneAsync<long>("select count(*) from notifications where entity_id = @kept", new { kept }));
        Assert.Equal(1, Metrics.Dropped(NotificationType.TOPUP_REJECTED) - before);
    }

    /// <summary>
    /// A notification without its cause's transaction is refused loudly, never written without a scope (Java
    /// <c>Propagation.MANDATORY</c>; <c>NotificationsTests.anEventOutsideATransactionIsRefusedRatherThanWrittenUnscoped</c>
    /// over the service itself).
    /// </summary>
    [Fact]
    public async Task ANotificationOutsideATransactionIsRefused()
    {
        var entity = Guid.NewGuid();
        await using var scope = api.Factory.Services.CreateAsyncScope();
        var notifications = scope.ServiceProvider.GetRequiredService<Api.Modules.Notifications.Notifications>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => notifications.SendAsync(NotificationType.WALLET_LOW_BALANCE,
            () => new Notice(Audience.Of(Scope.Platform, Role.OPS), NotificationEntityType.ALERT, entity, NotificationParams.Of())));
        Assert.Equal(0, await Owner.QueryOneAsync<long>("select count(*) from notifications where entity_id = @entity", new { entity }));
    }

    // ----------------------------------------------------------------------------------- M1 beyond the fueling path

    [Fact]
    public async Task ATopUpIsVerifiedAndCreditedAndATicketOpenedWhenTheirNotificationsAreRefused()
    {
        var company = await id.SeedCompanyAsync();
        var fleet = await id.SignInAsync(await id.SeedAsync(Role.FLEET_MANAGER, Scope.Company(company), false));
        var verifier = await id.SignInAsync(await id.SeedAsync(Role.OPS_MANAGER));
        var approver = await id.SignInAsync(await id.SeedAsync(Role.OPS_MANAGER));
        var topUp = await RequestTopUpAsync(fleet, 250_000);
        long Dropped() => Metrics.Dropped(NotificationType.TOPUP_AWAITING_APPROVAL) + Metrics.Dropped(NotificationType.TOPUP_CREDITED)
            + Metrics.Dropped(NotificationType.TICKET_OPENED);
        var before = Dropped();

        var trigger = await RefuseNotificationsAboutAsync(company);
        try
        {
            var verified = await id.AuthedAsync("POST", $"/api/v1/ops/topups/{topUp}/verify", verifier.AccessToken,
                "{\"receivedAmountPiastres\":240000}");
            Assert.True((int)verified.StatusCode == 200, await verified.Content.ReadAsStringAsync());
            var approved = await id.AuthedAsync("POST", $"/api/v1/ops/topups/{topUp}/approve", approver.AccessToken);
            Assert.True((int)approved.StatusCode == 200, await approved.Content.ReadAsStringAsync());
            var ticket = await id.AuthedAsync("POST", "/api/v1/company/tickets", fleet.AccessToken,
                "{\"type\":\"LOST_TAG\",\"subject\":\"Tag lost\",\"body\":\"The tag of car 12 fell off.\"}");
            Assert.True((int)ticket.StatusCode == 201, await ticket.Content.ReadAsStringAsync());
            var ticketId = (await ticket.JsonAsync()).GetProperty("ticket").GetProperty("id").GetString();

            // The money moved.
            Assert.Equal(240_000, await Owner.QueryOneAsync<long>("select balance_piastres from company_wallets where company_id = @company",
                new { company }));
            Assert.Equal("CONFIRMED", await Owner.QueryOneAsync<string>("select status from topup_requests where id = @topUp::uuid", new { topUp }));
            Assert.Equal(1, await Owner.QueryOneAsync<long>("select count(*) from tickets where id = @ticketId::uuid", new { ticketId }));
            // And nothing about it was written.
            Assert.Equal(0, await Owner.QueryOneAsync<long>(
                "select count(*) from notifications where audience_scope_id = @company or params ->> 'companyId' = @text",
                new { company, text = company.ToString() }));
        }
        finally
        {
            await DropAsync(trigger);
        }
        Assert.Equal(3, Dropped() - before);
    }

    // ------------------------------------------------------------------------------------------------------- helpers

    private DbTx? current;

    /// <summary>The cause's own statement in the open transaction: a platform notice the Ops user may write.</summary>
    private Task<int> PlantInTxAsync(Guid notificationId, Guid? entity = null) => current!.ExecuteAsync("""
        insert into notifications (id, type, audience_scope_type, audience_scope_id, audience_roles, entity_type, entity_id, params,
                                   actor_id, created_at)
        values (@notificationId, 'HARD_ALERT_RAISED', 'PLATFORM', null, '{OPS}', 'ALERT', @entity, '{}', app_user_id(), now())
        """, new { notificationId, entity = entity ?? notificationId });

    /// <summary>The notifications service on a transaction of its own in <paramref name="scope"/>, committed at the end.</summary>
    private async Task InScopeAsync(DbScopeContext scope, Func<Api.Modules.Notifications.Notifications, Task> work)
    {
        await using var services = api.Factory.Services.CreateAsyncScope();
        var session = services.ServiceProvider.GetRequiredService<DbSession>();
        await services.ServiceProvider.GetRequiredService<ITransactions>().RunAsync(scope, async tx =>
        {
            session.Attach(tx);
            current = tx;
            try
            {
                await work(services.ServiceProvider.GetRequiredService<Api.Modules.Notifications.Notifications>());
                return 0;
            }
            finally
            {
                session.Detach();
                current = null;
            }
        });
    }

    private static object Sample(string key) =>
        key.EndsWith("Id", StringComparison.Ordinal) ? Guid.NewGuid().ToString()
        : key.EndsWith("Piastres", StringComparison.Ordinal) ? 1_500
        : "LOW_BALANCE";

    /// <summary>A notification written as the owner, so only the table's own checks apply.</summary>
    private Task<int> PlantAsync(NotificationType type, Guid company, string @params) => Owner.ExecuteAsync("""
        insert into notifications (id, type, audience_scope_type, audience_scope_id, audience_roles, entity_type,
                                   entity_id, params, actor_id, created_at)
        values (gen_random_uuid(), @type, 'COMPANY', @company, '{FLEET_MANAGER}', 'TOPUP', gen_random_uuid(), @params::jsonb, null, now())
        """, new { type = type.ToString(), company, @params });

    private static Task<int> InsertAsync(NpgsqlConnection c, NpgsqlTransaction tx, NotificationType type, string @params) =>
        ExecuteAsync(c, tx, """
            insert into notifications (id, type, audience_scope_type, audience_scope_id, audience_roles, entity_type,
                                       entity_id, params, actor_id, created_at)
            values (gen_random_uuid(), @type, 'PLATFORM', null, '{OPS}', 'ALERT', gen_random_uuid(), @params::jsonb, app_user_id(), now())
            """, ("type", type.ToString()), ("params", @params));

    /// <summary>A trigger that refuses every notification addressed to this company or about it, until dropped.</summary>
    internal async Task<string> RefuseNotificationsAboutAsync(Guid company)
    {
        var name = "test_refuse_" + Guid.NewGuid().ToString("N");
        await Owner.ExecuteAsync($"create function {name}() returns trigger language plpgsql as $$ begin "
            + "raise exception 'notification refused by the test'; end $$");
        await Owner.ExecuteAsync($"create trigger {name} before insert on notifications for each row when ("
            + $"new.audience_scope_id = '{company}' or new.params ->> 'companyId' = '{company}') execute function {name}()");
        return name;
    }

    private async Task<string> RequestTopUpAsync(SignedIn member, long piastres)
    {
        var requested = await id.AuthedAsync("POST", "/api/v1/company/topups", member.AccessToken,
            $"{{\"amountPiastres\":{piastres},\"bankReference\":\"FT-{Guid.NewGuid()}\",\"transferDate\":\"{LedgerHttp.Today:yyyy-MM-dd}\"}}");
        Assert.True((int)requested.StatusCode == 201, await requested.Content.ReadAsStringAsync());
        return (await requested.JsonAsync()).GetProperty("id").GetString()!;
    }

    internal async Task DropAsync(string trigger)
    {
        await Owner.ExecuteAsync($"drop trigger {trigger} on notifications");
        await Owner.ExecuteAsync($"drop function {trigger}()");
    }
}
