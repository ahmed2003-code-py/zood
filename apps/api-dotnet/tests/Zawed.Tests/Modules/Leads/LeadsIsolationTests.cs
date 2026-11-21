using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zawed.Api.Modules.Leads;
using Zawed.Tests.Support;

namespace Zawed.Tests.Modules.Leads;

/// <summary>
/// Leads at the database layer (V22; Java <c>LeadsIsolationTests</c>), as the application's runtime role
/// (<c>zawed_app</c>): the public form's narrow door works without a scope, a lead is visible to a platform user only (never
/// to a company, an operator, or a POS route in the platform scope without a user), the runtime role cannot insert,
/// delete or change what the prospect sent, and closed leads are purged after their retention and never inside 90 days.
/// </summary>
public sealed class LeadsIsolationTests(SharedApi api) : IClassFixture<SharedApi>
{
    private Owner Owner => api.Factory.Owner;

    [Fact]
    public async Task TheFormsDoorWorksWithoutAScopeAndOnlyAPlatformUserSeesTheLead()
    {
        var lead = await SubmitAsTheFormAsync();
        var user = Guid.NewGuid();
        var tenant = Guid.NewGuid();
        Assert.Equal(0, await VisibleAsync(null, null, null, lead)); // no scope
        Assert.Equal(1, await VisibleAsync("PLATFORM", null, user, lead)); // a platform user
        Assert.Equal(0, await VisibleAsync("PLATFORM", null, null, lead)); // the platform scope without a user (every POS route)
        Assert.Equal(0, await VisibleAsync("COMPANY", tenant, user, lead)); // a company user
        Assert.Equal(0, await VisibleAsync("OPERATOR", tenant, user, lead)); // a station manager
    }

    [Fact]
    public async Task TheRuntimeRoleCannotInsertDeleteOrRewriteWhatTheProspectSent()
    {
        var lead = await SubmitAsTheFormAsync();
        var user = Guid.NewGuid();
        var insert = "insert into leads (id, reference, company_name, contact_name, phone, email, fleet_size, locale, last_requested_at, "
            + $"created_at, updated_at) values ('{Guid.NewGuid()}', 'L-99999999', 'Xx', 'Xx', '01001234567', 'x@example.test', '1-19', 'ar', now(), now(), now())";
        foreach (var sql in new[] { insert, $"delete from leads where id = '{lead}'", $"update leads set phone = '01009999999' where id = '{lead}'",
            $"update leads set request_count = 99 where id = '{lead}'", $"update leads set created_at = now() - interval '5 years' where id = '{lead}'" })
        {
            var refused = await Owner.AsRuntimeRoleAsync("PLATFORM", null, user, async (connection, tx) =>
                await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(sql, transaction: tx)));
            Assert.Contains("permission denied", refused.Message);
        }
    }

    [Fact]
    public async Task OnlyAPlatformUserMovesALeadAndOnlyInTheirOwnName()
    {
        var lead = await SubmitAsTheFormAsync();
        var user = Guid.NewGuid();
        string Move(Guid by) => $"update leads set status = 'CONTACTED', updated_at = now(), updated_by = '{by}' where id = '{lead}'";

        Assert.Equal(0, await UpdateAsync("COMPANY", Guid.NewGuid(), user, Move(user))); // a company
        Assert.Equal(0, await UpdateAsync("OPERATOR", Guid.NewGuid(), user, Move(user))); // an operator
        Assert.Equal(0, await UpdateAsync("PLATFORM", null, null, Move(user))); // a POS route
        var refused = await Owner.AsRuntimeRoleAsync("PLATFORM", null, user, async (connection, tx) =>
            await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(Move(Guid.NewGuid()), transaction: tx)));
        Assert.Contains("row-level security", refused.Message); // in another user's name
        Assert.Equal(1, await UpdateAsync("PLATFORM", null, user, Move(user))); // a platform user
    }

    [Fact]
    public async Task TheFormsDoorWritesOnlyValidValuesAndAlwaysANewLead()
    {
        var (connection, tx) = await RuntimeConnections.OpenAsync(Owner, null);
        await using (connection)
        await using (tx)
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => connection.QueryAsync(
                "select * from submit_lead(@id, 'Nile Co', 'Mona', '123', 'm@example.test', '1-19', 'ar')", new { id = Guid.NewGuid() }, tx));
            Assert.Contains("check constraint", refused.Message);
        }
        var lead = await SubmitAsTheFormAsync();
        Assert.Equal("NEW - 1 true true", await Owner.QueryOneAsync<string>("""
            select status || ' ' || coalesce(note, '-') || ' ' || request_count || ' ' || (updated_by is null) || ' ' || (closed_at is null)
            from leads where id = @lead
            """, new { lead }));
    }

    [Fact]
    public async Task ClosedLeadsArePurgedAfterTheirRetentionAndNothingElse()
    {
        var now = DateTimeOffset.UtcNow;
        var oldClosed = await LeadsOpsTests.PlantAsync(Owner, now.AddDays(-900), "CLOSED");
        await Owner.ExecuteAsync("update leads set closed_at = now() - interval '800 days' where id = @oldClosed", new { oldClosed });
        var recentlyClosed = await LeadsOpsTests.PlantAsync(Owner, now.AddDays(-900), "CLOSED");
        await Owner.ExecuteAsync("update leads set closed_at = now() - interval '100 days' where id = @recentlyClosed", new { recentlyClosed });
        var oldOpen = await LeadsOpsTests.PlantAsync(Owner, now.AddDays(-900), "QUALIFIED");

        var retention = api.Factory.Services.GetRequiredService<LeadRetention>();
        Assert.True(await retention.PurgeAsync() >= 1);
        Assert.Equal(0L, await Owner.QueryOneAsync<long>("select count(*) from leads where id = @oldClosed", new { oldClosed }));
        Assert.Equal(2L, await Owner.QueryOneAsync<long>("select count(*) from leads where id in (@recentlyClosed, @oldOpen)", new { recentlyClosed, oldOpen }));
        Assert.Equal(TimeSpan.FromDays(91), LeadRetention.Effective(TimeSpan.FromDays(1)));
        Assert.Equal(TimeSpan.FromDays(730), LeadRetention.Effective(TimeSpan.FromDays(730)));

        foreach (var cutoff in new[] { "now() - interval '30 days'", "now() - interval '89 days'", "null" })
        {
            var (connection, tx) = await RuntimeConnections.OpenAsync(Owner, null);
            await using (connection)
            await using (tx)
            {
                var refused = await Assert.ThrowsAsync<PostgresException>(() => connection.QueryAsync($"select purge_closed_leads({cutoff}, 100)", transaction: tx));
                Assert.Contains("90-day minimum", refused.Message);
            }
        }
        Assert.Equal(1L, await Owner.QueryOneAsync<long>("select count(*) from leads where id = @recentlyClosed", new { recentlyClosed }));

        // The job records its run under its own name, with the count it deleted.
        var run = await retention.RunNowAsync();
        Assert.Equal(Zawed.Api.Platform.Jobs.JobOutcome.SUCCESS, run.Outcome);
        Assert.True(run.Counts.ContainsKey("deleted"));
    }

    // ------------------------------------------------------------------------------------------------- helpers

    /// <summary>What the public form does: the definer function, on the application's role, with no scope.</summary>
    private async Task<Guid> SubmitAsTheFormAsync()
    {
        var token = Guid.NewGuid().ToString()[..8];
        var (connection, tx) = await RuntimeConnections.OpenAsync(Owner, null);
        await using (connection)
        await using (tx)
        {
            var (id, repeated) = await connection.QuerySingleAsync<(Guid, bool)>(
                "select lead_id, repeated from submit_lead(@id, @company, 'Mona Adel', @phone, @email, '20-49', 'ar')", new
                {
                    id = Guid.NewGuid(), company = "Isolation Co " + token,
                    phone = "011" + Random.Shared.Next(100_000_000).ToString("D8", System.Globalization.CultureInfo.InvariantCulture),
                    email = "iso-" + token + "@example.test",
                }, tx);
            Assert.False(repeated); // a new lead
            await tx.CommitAsync();
            return id;
        }
    }

    private Task<int> VisibleAsync(string? type, Guid? scopeId, Guid? user, Guid lead) => Owner.AsRuntimeRoleAsync(type, scopeId, user,
        (connection, tx) => connection.QuerySingleAsync<int>("select count(*)::int from leads where id = @lead", new { lead }, tx));

    private Task<int> UpdateAsync(string type, Guid? scopeId, Guid? user, string sql) =>
        Owner.AsRuntimeRoleAsync(type, scopeId, user, (connection, tx) => connection.ExecuteAsync(sql, transaction: tx));
}
