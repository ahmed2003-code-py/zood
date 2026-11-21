using Zawed.Api.Platform.Jobs;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Ledger;

/// <summary>
/// The check that every cached balance equals the sum of its ledger entries (PRD §4.2, plan §3.5; Java
/// <c>Reconciliation</c>): company wallets since B3, station payables since B6 (V9). The comparison itself is
/// <c>ledger_reconcile</c>: it runs as the database owner because the job has no principal, takes an advisory lock so
/// one instance runs it at a time (ADR 0011), and reads one snapshot per kind. A run that finds nothing is still
/// recorded, so "last run" is always answerable.
/// </summary>
public sealed class Reconciliation(IDbSession session, ReconcileRunner runner)
{
    internal sealed class RunRow
    {
        public Guid Id { get; set; }
        public string Trigger { get; set; } = "";
        public Guid? Triggered_By { get; set; }
        public DateTime Started_At { get; set; }
        public DateTime? Finished_At { get; set; }
        public int Wallets_Checked { get; set; }
        public int Payables_Checked { get; set; }
        public int Findings_Count { get; set; }

        public ReconciliationRunResponse ToRun() => new(Id, Enum.Parse<ReconciliationTrigger>(Trigger), Triggered_By,
            LedgerTime.Utc(Started_At), LedgerTime.Utc(Finished_At), Wallets_Checked, Payables_Checked, Findings_Count);
    }

    private sealed class FindingRow
    {
        public Guid Id { get; set; }
        public Guid Run_Id { get; set; }
        public Guid? Company_Id { get; set; }
        public Guid? Station_Id { get; set; }
        public string Kind { get; set; } = "";
        public long? Cached_Balance_Piastres { get; set; }
        public long? Ledger_Balance_Piastres { get; set; }
        public long? Difference_Piastres { get; set; }
        public DateTime Created_At { get; set; }

        public ReconciliationFindingResponse ToFinding() => new(Id, Run_Id, Company_Id, Station_Id, Enum.Parse<FindingKind>(Kind),
            Cached_Balance_Piastres, Ledger_Balance_Piastres, Difference_Piastres, LedgerTime.Utc(Created_At));
    }

    /// <summary>The Super Admin's "run now".</summary>
    public async Task<Result<ReconciliationRunResponse>> RunNowAsync(ZawedPrincipal actor, CancellationToken ct) =>
        await runner.RunAsync(session.Current, ReconciliationTrigger.MANUAL, actor, ct) is { } run
            ? Result<ReconciliationRunResponse>.Created(run)
            : Result<ReconciliationRunResponse>.Conflict("RECONCILIATION_RUNNING", "A reconciliation run is already in progress");

    public async Task<Result<Page<ReconciliationRunResponse>>> RunsAsync(string? cursor, int? limit, CancellationToken ct)
    {
        var size = Page<ReconciliationRunResponse>.Limit(limit);
        var rows = await session.Current.QueryAsync<RunRow>("""
            select * from reconciliation_runs
            where (cast(@cursor as uuid) is null or id < @cursor)
            order by id desc limit @limit
            """, new { cursor = Cursors.Parse(cursor), limit = size + 1 }, ct);
        return Page<ReconciliationRunResponse>.Of(rows.Select(r => r.ToRun()).ToList(), size, r => r.Id);
    }

    public async Task<Result<Page<ReconciliationFindingResponse>>> FindingsAsync(Guid runId, string? cursor, int? limit, CancellationToken ct)
    {
        var tx = session.Current;
        if (await tx.QuerySingleOrDefaultAsync<int?>("select 1 from reconciliation_runs where id = @id", new { id = runId }, ct) is null)
        {
            return Result<Page<ReconciliationFindingResponse>>.NotFound("Reconciliation run");
        }
        var size = Page<ReconciliationFindingResponse>.Limit(limit);
        var rows = await tx.QueryAsync<FindingRow>("""
            select * from reconciliation_findings
            where run_id = @run and (cast(@cursor as uuid) is null or id < @cursor)
            order by id desc limit @limit
            """, new { run = runId, cursor = Cursors.Parse(cursor), limit = size + 1 }, ct);
        return Page<ReconciliationFindingResponse>.Of(rows.Select(r => r.ToFinding()).ToList(), size, f => f.Id);
    }
}

/// <summary>
/// The call to <c>ledger_reconcile</c>, shared by the Super Admin's "run now" (in the request's transaction) and the
/// nightly job (in its own, with no scope).
/// </summary>
public sealed class ReconcileRunner(TimeProvider clock, IHttpContextAccessor http)
{
    /// <summary>
    /// Runs the comparison now in <paramref name="tx"/>. Null when another run holds the lock, or, for a scheduled run,
    /// when tonight's (Cairo) scheduled run is already recorded.
    /// </summary>
    public async Task<ReconciliationRunResponse?> RunAsync(DbTx tx, ReconciliationTrigger trigger, ZawedPrincipal? actor, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var row = await tx.QuerySingleOrDefaultAsync<Reconciliation.RunRow>("select * from ledger_reconcile(@id, @trigger, @actor, @role, @now, @requestId)", new
        {
            id = Guid.CreateVersion7(now),
            trigger = trigger.ToString(),
            actor = actor?.UserId,
            role = actor?.Role.ToString(),
            now = now.UtcDateTime,
            requestId = RequestIds.Current(http.HttpContext),
        }, ct);
        return row?.ToRun();
    }

}

/// <summary>
/// The nightly reconciliation (Java <c>Reconciliation.nightly</c>, <c>@Scheduled(cron = "0 30 2 * * *", zone =
/// "Africa/Cairo")</c>): every night at 02:30 Cairo time, when fueling is at its quietest. Recorded in <c>job_runs</c>
/// (ADR 0028): a run that another instance's lock (or tonight's earlier run) turned away is <c>SKIPPED_LOCKED</c>, an
/// exception is <c>FAILED</c>, and the health check turns DOWN when no run has succeeded for
/// <c>zawed.jobs.max-age.ledger-reconciliation</c> (27 hours by default: on the night Egypt's clocks go back two runs
/// are 25 hours apart, and a run may take a while; a missed night still shows within three hours).
/// <para>The job has no principal and no scope: <c>ledger_reconcile</c> is <c>SECURITY DEFINER</c>.</para>
/// </summary>
public sealed class ReconciliationJob(Jobs jobs, ITransactions transactions, ReconcileRunner reconciliation, TimeProvider clock,
    ILogger<ReconciliationJob> logger)
    : ScheduledJob(jobs, "ledger-reconciliation", TimeSpan.FromHours(27))
{
    /// <summary>02:30 local time in <see cref="TopUps.BusinessZone"/>.</summary>
    public static readonly TimeSpan At = new(2, 30, 0);

    // Not used: the schedule is a wall-clock time, not an interval (see ExecuteAsync).
    protected override TimeSpan InitialDelay => TimeSpan.Zero;

    protected override TimeSpan Interval => TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = clock.GetUtcNow();
                await Task.Delay(NextRun(now) - now, clock, stoppingToken);
                await RunNowAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
    }

    /// <summary>The next 02:30 in Cairo strictly after <paramref name="now"/>, as a cron trigger fires.</summary>
    public static DateTimeOffset NextRun(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, TopUps.BusinessZone);
        for (var day = DateOnly.FromDateTime(local.DateTime); ; day = day.AddDays(1))
        {
            var wall = day.ToDateTime(TimeOnly.FromTimeSpan(At));
            if (TopUps.BusinessZone.IsInvalidTime(wall))
            {
                continue; // 02:30 does not exist that night (a spring-forward gap): cron skips it
            }
            var at = new DateTimeOffset(wall, TopUps.BusinessZone.GetUtcOffset(wall));
            if (at > now)
            {
                return at;
            }
        }
    }

    protected override async Task<JobResult> RunOnceAsync(CancellationToken ct)
    {
        var run = await transactions.RunAsync(DbScopeContext.None, tx => reconciliation.RunAsync(tx, ReconciliationTrigger.SCHEDULED, null, ct), ct: ct);
        if (run is null)
        {
            logger.LogInformation("Reconciliation skipped: another instance is running it or already ran it tonight");
            return JobResult.SkippedLocked();
        }
        if (run.FindingsCount > 0)
        {
            // A difference between a cached balance and the ledger is never routine: it must reach a person. Until the
            // alerts module raises it, this is the error the logs are watched for.
            logger.LogError("Reconciliation {Run} found {Findings} difference(s) across {Wallets} wallets and {Payables} station payables; "
                + "see the Ops reconciliation page", run.Id, run.FindingsCount, run.WalletsChecked, run.PayablesChecked);
        }
        else
        {
            logger.LogInformation("Reconciliation {Run}: {Wallets} wallets and {Payables} station payables checked, no difference",
                run.Id, run.WalletsChecked, run.PayablesChecked);
        }
        return JobResult.Success(new Dictionary<string, long>
        {
            ["wallets"] = run.WalletsChecked, ["payables"] = run.PayablesChecked, ["findings"] = run.FindingsCount,
        });
    }
}
