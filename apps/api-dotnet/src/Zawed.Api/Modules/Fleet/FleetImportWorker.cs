using System.Text.Json;
using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Fleet;

/// <summary>
/// Runs confirmed imports (ADR 0026; Java <c>FleetImportWorker</c>). PostgreSQL is the queue: a job is claimed with
/// <c>FOR UPDATE SKIP LOCKED</c> and a lease, so two instances never run one job and a crashed instance's job is picked up
/// again once its lease runs out.
/// <para>Each row is its own short transaction: the vehicle, its limits and the row's outcome commit or roll back together.
/// Resuming therefore starts at the first row still <c>VALID</c>, and nothing is created twice; the plate's unique index is
/// the last defence if anything else registered the plate meanwhile (the row then ends as <c>EXISTS</c>). A tick processes
/// a bounded number of rows and renews the lease as it goes.</para>
/// <para>Scope: the worker has no request, so it runs as the platform on behalf of the Ops user who confirmed the job,
/// recorded on the job (<see cref="DbScopeContext.Escalate"/>, a reviewed entry in <c>ArchitectureTests</c>). Every insert
/// still passes row-level security and the same-tenant triggers, for the job's own company.</para>
/// <para>Not a <c>ScheduledJob</c>: it polls every two seconds, as the Java <c>@Scheduled</c> worker did, and like it leaves
/// no <c>job_runs</c> record (a record per poll would flood the table and add a job Java never declared).
/// <c>Zawed:FleetImport:WorkerEnabled</c> (default true) and <c>Zawed:FleetImport:PollInterval</c> (default 2 s) are the
/// Java <c>zawed.fleet-import.*</c> settings.</para>
/// </summary>
public sealed class FleetImportWorker(IServiceScopeFactory scopes, ITransactions transactions, TimeProvider clock, IConfiguration configuration,
    ILogger<FleetImportWorker> log) : BackgroundService
{
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    public const int RowsPerTick = 250;

    private static readonly HashSet<string> Counters = ["imported_count", "existing_count", "failed_count"];

    private readonly string owner = $"{Environment.ProcessId}@{Environment.MachineName}:{Guid.CreateVersion7(clock.GetUtcNow())}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Zawed:FleetImport:WorkerEnabled", true))
        {
            return;
        }
        var interval = configuration.GetValue("Zawed:FleetImport:PollInterval", TimeSpan.FromSeconds(2));
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(RowsPerTick, owner, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError("Fleet import worker tick failed: {Error}", ex.GetType().FullName);
                }
                await Task.Delay(interval, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown: the row in progress committed or rolled back whole.
        }
    }

    private sealed record Claim(Guid JobId, Guid CompanyId, Guid ConfirmedBy, string Role);

    /// <summary>Claims one job and processes up to <paramref name="maxRows"/> of it; the job worked on, or null.</summary>
    public async Task<Guid?> RunOnceAsync(int maxRows, string workerId, CancellationToken ct = default)
    {
        var claim = await transactions.RunAsync(DbScopeContext.Escalate(Scope.Platform, null), tx => ClaimAsync(tx, workerId, ct), ct: ct);
        if (claim is null)
        {
            return null;
        }
        var actor = new ZawedPrincipal(claim.ConfirmedBy, Guid.Empty, Enum.Parse<Role>(claim.Role), Scope.Platform, false);
        var rows = await transactions.RunAsync(As(claim), async tx => (await tx.QueryAsync<(int Number, string Data)>("""
            select row_number, data::text from fleet_import_rows
            where job_id = @job and status = 'VALID' order by row_number limit @limit
            """, new { job = claim.JobId, limit = maxRows }, ct)).ToList(), ct: ct);
        foreach (var row in rows)
        {
            if (!await ProcessAsync(claim, actor, row.Number, row.Data, workerId, ct))
            {
                return claim.JobId; // another worker owns the job now; it finishes it
            }
        }
        if (rows.Count < maxRows)
        {
            await transactions.RunAsync(As(claim), tx => FinishAsync(tx, claim, workerId, ct), ct: ct);
        }
        return claim.JobId;
    }

    private async Task<Claim?> ClaimAsync(DbTx tx, string workerId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var claimed = (await tx.QueryAsync<(Guid, Guid, Guid, string)>("""
            update fleet_import_jobs set status = 'RUNNING', lease_owner = @owner, lease_until = @until,
                   started_at = coalesce(started_at, @now), updated_at = @now
            where id = (select id from fleet_import_jobs
                        where status = 'QUEUED' or (status = 'RUNNING' and (lease_until < @now or lease_owner = @owner))
                        order by confirmed_at, id limit 1 for update skip locked)
            returning id, company_id, confirmed_by, confirmed_by_role
            """, new { owner = workerId, until = (now + Lease).UtcDateTime, now = now.UtcDateTime }, ct)).ToList();
        return claimed is [var c] ? new Claim(c.Item1, c.Item2, c.Item3, c.Item4) : null;
    }

    /// <summary>A row this worker no longer owns: another worker took the job after this one's lease ran out.</summary>
    private sealed class RowTakenOver() : Exception("The import row is no longer pending");

    /// <summary>A refusal of the vehicle or a limit: the row's transaction rolls back whole, and the refusal is its outcome.</summary>
    private sealed class RowRefused(ResultError error) : Exception(error.Code)
    {
        public ResultError Error { get; } = error;
    }

    /// <summary>Processes one row; every outcome is recorded on the row, so a job never stalls on it. False when the job was lost.</summary>
    private async Task<bool> ProcessAsync(Claim claim, ZawedPrincipal actor, int number, string data, string workerId, CancellationToken ct)
    {
        try
        {
            await AttemptAsync(claim, actor, number, data, workerId, ct);
            return true;
        }
        catch (RowTakenOver)
        {
            return false;
        }
        catch (RowRefused refused)
        {
            // The row's transaction rolled back whole; record why in a transaction of its own.
            var exists = refused.Error.Code is "PLATE_IN_USE" or "FLEET_CODE_IN_USE";
            return await RecordAsync(claim, number, exists ? ImportRowStatus.EXISTS : ImportRowStatus.FAILED,
                new FleetImportRowError(refused.Error.Field ?? "row", refused.Error.Code), exists ? "existing_count" : "failed_count", workerId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Anything else (a database refusal validation did not foresee, a bug) fails this row only. Rethrowing would leave
            // the row VALID, and the job would be reclaimed and fail on it forever. Neither the row's data nor the exception's
            // message is logged: a PostgreSQL constraint error carries the whole failing row, which is the company's data.
            log.LogError("Fleet import {Job} row {Row} failed unexpectedly: {Error} (SQLState {State})", claim.JobId, number,
                ex.GetType().Name, DbErrors.Postgres(ex)?.SqlState ?? "none");
            return await RecordAsync(claim, number, ImportRowStatus.FAILED, new FleetImportRowError("row", "INTERNAL"), "failed_count",
                workerId, ct);
        }
    }

    private async Task<bool> RecordAsync(Claim claim, int number, ImportRowStatus status, FleetImportRowError error, string counter,
        string workerId, CancellationToken ct)
    {
        try
        {
            await transactions.RunAsync(As(claim), async tx =>
            {
                await OutcomeAsync(tx, claim, number, status, null, [error], counter, workerId, ct);
                return true;
            }, ct: ct);
            return true;
        }
        catch (RowTakenOver)
        {
            return false;
        }
    }

    /// <summary>The vehicle, its limits and the row's outcome, in one transaction: all of it commits, or none.</summary>
    private Task AttemptAsync(Claim claim, ZawedPrincipal actor, int number, string data, string workerId, CancellationToken ct) =>
        transactions.RunAsync(As(claim), async tx =>
        {
            await using var services = scopes.CreateAsyncScope();
            var session = services.ServiceProvider.GetRequiredService<DbSession>();
            session.Attach(tx);
            try
            {
                var vehicles = services.ServiceProvider.GetRequiredService<Vehicles>();
                var v = JsonSerializer.Deserialize<ImportValues>(data, FleetImports.Json)
                    ?? throw new JsonException("An import row without values");
                var created = await vehicles.CreateAsync(actor, claim.CompanyId, new Vehicles.Draft(v.DepartmentId, v.Plate, v.Make, v.Model,
                    v.Year, v.FuelType, v.TankCapacityMl, v.Tier, v.VipMonthlyCeilingMl, v.FleetCode), ct);
                var vehicle = created.Data ?? throw new RowRefused(created.Error!);
                foreach (var (period, max) in new[] { (LimitPeriod.DAY, v.DailyLimitMl), (LimitPeriod.WEEK, v.WeeklyLimitMl),
                    (LimitPeriod.MONTH, v.MonthlyLimitMl) })
                {
                    if (max is { } limit && await vehicles.SetLimitAsync(actor, vehicle.Id, LimitKind.VOLUME, period, limit, ct) is { IsSuccess: false } refused)
                    {
                        throw new RowRefused(refused.Error!);
                    }
                }
                await OutcomeAsync(tx, claim, number, ImportRowStatus.IMPORTED, vehicle.Id, [], "imported_count", workerId, ct);
                return true;
            }
            finally
            {
                session.Detach();
            }
        }, ct: ct);

    /// <summary>The row's outcome, its job counter and the lease, in the row's own transaction.</summary>
    private async Task OutcomeAsync(DbTx tx, Claim claim, int number, ImportRowStatus status, Guid? vehicleId,
        IReadOnlyList<FleetImportRowError> errors, string counter, string workerId, CancellationToken ct)
    {
        if (!Counters.Contains(counter))
        {
            throw new ArgumentException("Not a job counter", nameof(counter));
        }
        var now = clock.GetUtcNow();
        var updated = await tx.ExecuteAsync("""
            update fleet_import_rows set status = @status, vehicle_id = @vehicle, errors = cast(@errors as jsonb),
                   processed_at = @now
            where job_id = @job and row_number = @row and status = 'VALID'
            """, new
        {
            status = status.ToString(), vehicle = vehicleId, errors = JsonSerializer.Serialize(errors, FleetImports.Json), now = now.UtcDateTime,
            job = claim.JobId, row = number,
        }, ct);
        if (updated != 1)
        {
            // Another worker took this row after a lease expired: undo this attempt entirely.
            throw new RowTakenOver();
        }
        await tx.ExecuteAsync($"update fleet_import_jobs set {counter} = {counter} + 1, lease_until = @until, updated_at = @now "
            + "where id = @job and lease_owner = @owner",
            new { until = (now + Lease).UtcDateTime, now = now.UtcDateTime, job = claim.JobId, owner = workerId }, ct);
    }

    private async Task<bool> FinishAsync(DbTx tx, Claim claim, string workerId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var counts = await tx.QuerySingleAsync<(long Pending, long Imported, long Existing, long Failed, long Total)>("""
            select count(*) filter (where status = 'VALID') as pending,
                   count(*) filter (where status = 'IMPORTED') as imported,
                   count(*) filter (where status = 'EXISTS') as existing,
                   count(*) filter (where status = 'FAILED') as failed,
                   count(*) as total
            from fleet_import_rows where job_id = @job
            """, new { job = claim.JobId }, ct);
        if (counts.Pending > 0)
        {
            return false; // rows remain: the next tick continues
        }
        var status = counts.Imported == counts.Total ? ImportStatus.COMPLETED : ImportStatus.COMPLETED_WITH_ERRORS;
        // The counters are recomputed from the rows: a row committed by a worker whose lease had just been taken over updates
        // no counter (its counter update matches no lease), so the running totals can fall short. The rows are the record.
        var updated = await tx.ExecuteAsync("""
            update fleet_import_jobs set status = @status, finished_at = @now, lease_owner = null, lease_until = null,
                   imported_count = @imported, existing_count = @existing, failed_count = @failed, updated_at = @now
            where id = @job and status = 'RUNNING' and lease_owner = @owner
            """, new
        {
            imported = (int)counts.Imported, existing = (int)counts.Existing, failed = (int)counts.Failed, status = status.ToString(),
            now = now.UtcDateTime, job = claim.JobId, owner = workerId,
        }, ct);
        if (updated == 1)
        {
            await using var services = scopes.CreateAsyncScope();
            await services.ServiceProvider.GetRequiredService<IAuditLog>().RecordAsync(tx, new AuditActor(claim.ConfirmedBy, Enum.Parse<Role>(claim.Role)),
                "FLEET_IMPORT_COMPLETED", "FLEET_IMPORT", claim.JobId, Scope.Company(claim.CompanyId), new Dictionary<string, object?>
                {
                    ["imported"] = (int)counts.Imported, ["existing"] = (int)counts.Existing, ["failed"] = (int)counts.Failed,
                    ["status"] = status.ToString(),
                }, ct);
            log.LogInformation("Fleet import {Job} finished: {Imported} imported, {Existing} existing, {Failed} failed", claim.JobId,
                counts.Imported, counts.Existing, counts.Failed);
        }
        return true;
    }

    /// <summary>The platform scope on behalf of the Ops user who confirmed the job.</summary>
    private static DbScopeContext As(Claim claim) => DbScopeContext.Escalate(Scope.Platform, claim.ConfirmedBy);
}
