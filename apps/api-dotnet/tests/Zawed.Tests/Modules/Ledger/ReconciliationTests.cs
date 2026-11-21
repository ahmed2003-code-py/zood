using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zawed.Api.Modules.Ledger;
using Zawed.Api.Platform.Jobs;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Ledger.LedgerHttp;

namespace Zawed.Tests.Modules.Ledger;

/// <summary>
/// The nightly comparison of cached balances with the ledger (PRD §4.2; Java <c>ReconciliationTests</c>), run on demand
/// and by the job.
/// </summary>
public sealed class ReconciliationTests(LedgerApi api) : IClassFixture<LedgerApi>
{
    private readonly IdentityTestSupport id = new(api.Factory);

    [Fact]
    public async Task ACorruptedCachedBalanceIsFoundAndACleanWalletIsNot()
    {
        var admin = await id.SignInAsync(await id.SeedAsync(Role.SUPER_ADMIN));
        var ops = await id.SignInAsync(await id.SeedAsync(Role.OPS));
        var company = await id.SeedCompanyAsync();
        await new LedgerHttp(id).FundAsync(ops, company, 80_000);

        // Clean: this wallet agrees with its ledger.
        var clean = await id.AuthedAsync("POST", "/api/v1/ops/reconciliation/runs", admin.AccessToken);
        Assert.True((int)clean.StatusCode == 201, await clean.Content.ReadAsStringAsync());
        var cleanRun = await TextAsync(clean, "id");
        Assert.True((await BodyAsync(clean)).GetProperty("walletsChecked").GetInt32() > 0);
        Assert.DoesNotContain(await MismatchesAsync(ops, cleanRun), f => f.GetProperty("companyId").GetString() == company.ToString());

        // The owner corrupts the cache by 1,234 piastres (nothing else can: the ledger is append only).
        await api.Owner.ExecuteAsync("update company_wallets set balance_piastres = balance_piastres + 1234 where company_id = @company", new { company });
        var dirty = await id.AuthedAsync("POST", "/api/v1/ops/reconciliation/runs", admin.AccessToken);
        Assert.Equal(201, (int)dirty.StatusCode);
        var dirtyRun = await TextAsync(dirty, "id");
        var found = Assert.Single(await MismatchesAsync(ops, dirtyRun), f => f.GetProperty("companyId").GetString() == company.ToString());
        Assert.Equal(81_234, found.GetProperty("cachedBalancePiastres").GetInt64());
        Assert.Equal(80_000, found.GetProperty("ledgerBalancePiastres").GetInt64());
        Assert.Equal(1_234, found.GetProperty("differencePiastres").GetInt64());
        Assert.Equal(JsonValueKind.Null, found.GetProperty("stationId").ValueKind);

        // Ops reads the runs, newest first, and the findings; the run is audited with its actor.
        var runs = await BodyAsync(await id.AuthedAsync("GET", "/api/v1/ops/reconciliation/runs?limit=5", ops.AccessToken));
        var ids = runs.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToList();
        Assert.True(ids.IndexOf(dirtyRun) >= 0 && ids.IndexOf(dirtyRun) < ids.IndexOf(cleanRun), string.Join(",", ids));
        var dirtyRow = runs.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("id").GetString() == dirtyRun);
        Assert.Equal("MANUAL", dirtyRow.GetProperty("trigger").GetString());
        Assert.Equal(admin.UserId.ToString(), dirtyRow.GetProperty("triggeredBy").GetString());
        Assert.True(dirtyRow.GetProperty("findingsCount").GetInt32() >= 1);
        Assert.Equal(admin.UserId, await api.Owner.QueryOneAsync<Guid>(
            "select actor_id from audit_log where action = 'RECONCILIATION_RUN' and entity_id = @run::uuid", new { run = dirtyRun }));

        // Repaired: the wallet no longer appears.
        await api.Owner.ExecuteAsync("update company_wallets set balance_piastres = balance_piastres - 1234 where company_id = @company", new { company });
        var repairedRun = await TextAsync(await id.AuthedAsync("POST", "/api/v1/ops/reconciliation/runs", admin.AccessToken), "id");
        Assert.DoesNotContain(await MismatchesAsync(ops, repairedRun), f => f.GetProperty("companyId").GetString() == company.ToString());

        // An unknown run is 404; Ops may not run one (Super Admin only).
        await Http.AssertErrorAsync(await id.AuthedAsync("GET", $"/api/v1/ops/reconciliation/runs/{Guid.NewGuid()}/findings", ops.AccessToken), 404, "NOT_FOUND");
        await Http.AssertErrorAsync(await id.AuthedAsync("POST", "/api/v1/ops/reconciliation/runs", ops.AccessToken), 403, "FORBIDDEN");
    }

    [Fact]
    public async Task OnlyOneRunAtATime()
    {
        var admin = await id.SignInAsync(await id.SeedAsync(Role.SUPER_ADMIN));
        // Another instance holding the job's lock, for as long as this connection is open.
        await using (var other = new NpgsqlConnection(api.Owner.ConnectionString))
        {
            await other.OpenAsync();
            await using (var command = new NpgsqlCommand("select pg_advisory_lock(hashtext('ledger_reconcile'))", other))
            {
                await command.ExecuteNonQueryAsync();
            }
            await Http.AssertErrorAsync(await id.AuthedAsync("POST", "/api/v1/ops/reconciliation/runs", admin.AccessToken), 409, "RECONCILIATION_RUNNING");
            await using (var command = new NpgsqlCommand("select pg_advisory_unlock(hashtext('ledger_reconcile'))", other))
            {
                await command.ExecuteNonQueryAsync();
            }
        }
        Assert.Equal(201, (int)(await id.AuthedAsync("POST", "/api/v1/ops/reconciliation/runs", admin.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task TheScheduledJobRunsOncePerCairoNightWithNoPrincipalAndIsAudited()
    {
        // Start from a night with no scheduled run (other tests may have run one today).
        await api.Owner.ExecuteAsync("""
            delete from reconciliation_findings where run_id in (select id from reconciliation_runs
              where trigger = 'SCHEDULED' and (started_at at time zone 'Africa/Cairo')::date
                                           = (now() at time zone 'Africa/Cairo')::date)
            """);
        await api.Owner.ExecuteAsync("""
            delete from reconciliation_runs where trigger = 'SCHEDULED'
              and (started_at at time zone 'Africa/Cairo')::date = (now() at time zone 'Africa/Cairo')::date
            """);
        // As the scheduler calls it: no request, no principal, no scope.
        var job = api.Factory.Services.GetRequiredService<ReconciliationJob>();
        var before = await api.CountAsync("select count(*) from reconciliation_runs where trigger = 'SCHEDULED'");
        var result = await job.RunNowAsync();
        Assert.Equal(JobOutcome.SUCCESS, result.Outcome);
        var latest = await api.Owner.QueryOneAsync<Guid>(
            "select id from reconciliation_runs where trigger = 'SCHEDULED' order by started_at desc, id desc limit 1");
        Assert.Equal(before + 1, await api.CountAsync("select count(*) from reconciliation_runs where trigger = 'SCHEDULED'"));
        Assert.Null(await api.Owner.QueryOneAsync<Guid?>("select triggered_by from reconciliation_runs where id = @latest", new { latest }));
        Assert.NotNull(await api.Owner.QueryOneAsync<DateTime?>("select finished_at from reconciliation_runs where id = @latest", new { latest }));
        Assert.Equal(1, await api.CountAsync(
            "select count(*) from audit_log where action = 'RECONCILIATION_RUN' and entity_id = @latest and actor_id is null", new { latest }));
        // B7 W4 (ADR 0028): the run is recorded for the health check, with numbers only.
        Assert.Equal("SUCCESS truetruetrue", await api.Owner.QueryOneAsync<string>("""
            select outcome || ' ' || (counts ->> 'wallets' ~ '^[0-9]+$') || (counts ->> 'payables' ~ '^[0-9]+$')
                   || (counts ->> 'findings' ~ '^[0-9]+$')
            from job_runs where job_name = 'ledger-reconciliation' order by started_at desc, id desc limit 1
            """));

        // A second instance reaching 02:30 after the first finds tonight's run done.
        Assert.Equal(JobOutcome.SKIPPED_LOCKED, (await job.RunNowAsync()).Outcome);
        Assert.Equal("SKIPPED_LOCKED", await api.Owner.QueryOneAsync<string>("""
            select outcome from job_runs where job_name = 'ledger-reconciliation' order by started_at desc, id desc limit 1
            """)); // recorded as skipped, not as a success
        var runner = api.Factory.Services.GetRequiredService<ReconcileRunner>();
        var transactions = api.Factory.Services.GetRequiredService<ITransactions>();
        Assert.Null(await transactions.RunAsync(DbScopeContext.None, tx => runner.RunAsync(tx, ReconciliationTrigger.SCHEDULED, null, default)));
        Assert.Equal(before + 1, await api.CountAsync("select count(*) from reconciliation_runs where trigger = 'SCHEDULED'"));

        // A run by hand is always allowed.
        var manual = await transactions.RunAsync(DbScopeContext.None, tx => runner.RunAsync(tx, ReconciliationTrigger.MANUAL, null, default));
        Assert.NotNull(manual);
        Assert.Equal(ReconciliationTrigger.MANUAL, manual.Trigger);
    }

    [Fact]
    public void TheNightlyRunIsAt0230CairoTime()
    {
        // Beyond the Java class: the cron "0 30 2 * * *" in Africa/Cairo, across Egypt's summer and winter offsets.
        var winter = DateTimeOffset.Parse("2026-01-10T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(DateTimeOffset.Parse("2026-01-11T00:30:00Z", System.Globalization.CultureInfo.InvariantCulture), ReconciliationJob.NextRun(winter));
        var summer = DateTimeOffset.Parse("2026-07-10T23:29:59Z", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(DateTimeOffset.Parse("2026-07-10T23:30:00Z", System.Globalization.CultureInfo.InvariantCulture), ReconciliationJob.NextRun(summer));
        var exactly = DateTimeOffset.Parse("2026-07-10T23:30:00Z", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(exactly.AddDays(1), ReconciliationJob.NextRun(exactly));
    }

    private async Task<List<JsonElement>> MismatchesAsync(SignedIn ops, string run)
    {
        var findings = await id.AuthedAsync("GET", $"/api/v1/ops/reconciliation/runs/{run}/findings?limit=100", ops.AccessToken);
        Assert.True((int)findings.StatusCode == 200, await findings.Content.ReadAsStringAsync());
        return (await BodyAsync(findings)).GetProperty("items").EnumerateArray()
            .Where(f => f.GetProperty("kind").GetString() == "BALANCE_MISMATCH").ToList();
    }
}
