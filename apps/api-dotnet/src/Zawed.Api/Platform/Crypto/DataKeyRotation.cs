using System.Text.RegularExpressions;
using Npgsql;
using Zawed.Api.Platform.Config;
using Zawed.Api.Platform.Jobs;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;

namespace Zawed.Api.Platform.Crypto;

/// <summary>
/// A column that holds <see cref="DataEncryptor"/> values (Java <c>EncryptedColumn</c>), registered by the module that
/// owns it (<c>services.AddSingleton(new EncryptedColumn(...))</c> in its <c>IZawedModule</c>), so that
/// <see cref="DataKeyRotation"/> can re-encrypt it without depending on that module.
/// <para>The table's key is its <c>id</c> (uuid). <paramref name="Binding"/> must return exactly the associated data
/// the owning module's own code passes for that row: the re-encrypted value is bound to it, so any other binding would
/// make the value unreadable where it is used. Point it at the module's own binding method, never at a copy.</para>
/// </summary>
/// <param name="Table">the table, a plain lower-case SQL identifier</param>
/// <param name="Column">the bytea column, a plain lower-case SQL identifier</param>
/// <param name="Binding">the associated data for the row with this id</param>
public sealed partial record EncryptedColumn
{
    // The names are put into SQL text, so they are checked here, once, at startup.
    [GeneratedRegex(@"^[a-z_][a-z0-9_]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    public EncryptedColumn(string Table, string Column, Func<Guid, byte[]> Binding)
    {
        if (Table is null || !Identifier().IsMatch(Table) || Column is null || !Identifier().IsMatch(Column))
        {
            throw new ArgumentException("An encrypted column is named by plain lower-case identifiers");
        }
        this.Table = Table;
        this.Column = Column;
        this.Binding = Binding ?? throw new ArgumentNullException(nameof(Binding));
    }

    public string Table { get; }
    public string Column { get; }
    public Func<Guid, byte[]> Binding { get; }

    /// <summary><c>table.column</c>, as the progress log names it.</summary>
    public string Name => Table + "." + Column;
}

/// <summary>
/// Moves every encrypted value to the current data key (ADR 0027; Java <c>DataKeyRotation</c>), so a previous key can
/// be retired.
/// <para>For each <see cref="EncryptedColumn"/>, values whose key id is lower than the current one are read with their
/// own key and written again with the current key, bound to the same row. Nothing else in the row changes. The database
/// allows exactly this change and nothing more (V20).</para>
/// <para>The state is the data itself (the key id byte), so a run is idempotent and resumable. Work is done in small
/// batches, each its own short transaction: the rows are locked <c>FOR NO KEY UPDATE SKIP LOCKED</c>, so the job never
/// waits for an application transaction and cannot deadlock with the fueling path's wallet-then-tag order; it does not
/// conflict with the <c>FOR KEY SHARE</c> lock a new fueling's foreign key takes on its tag. A row another transaction
/// holds is skipped and taken on the next run. Two instances running it at once only share the work; a value is
/// updated with a compare-and-set on its old bytes, so it is never re-encrypted twice. A value that cannot be read is
/// left exactly as it is and counted.</para>
/// <para>The database accepts a re-encryption only into the deployment's current key id, which <see cref="DataKeyState"/>
/// records at every start. An instance whose current key is not that one does nothing and says so.</para>
/// <para>Progress is one log line per run with counts only, never a value.</para>
/// <para>Scope escalation (reviewed, <c>ArchitectureTests</c>): it has no request and no user, and works across every
/// tenant, so it runs as the platform. It reads and writes only the encrypted columns.</para>
/// </summary>
public sealed class DataKeyRotation
{
    /// <summary>Values per transaction: a batch holds at most this many row locks, for a few milliseconds.</summary>
    public const int Batch = 100;

    /// <summary>A run stops a column after this many batches (10,000 values) and continues on the next run.</summary>
    public const int MaxBatchesPerRun = 100;

    private static readonly DbScopeContext PlatformScope = DbScopeContext.Escalate(Scope.Platform, null);

    private readonly ITransactions transactions;
    private readonly DataEncryptor encryptor;
    private readonly IReadOnlyList<EncryptedColumn> columns;
    private readonly ILogger<DataKeyRotation> logger;
    private int settledReported;

    public DataKeyRotation(ITransactions transactions, DataEncryptor encryptor, IEnumerable<EncryptedColumn> columns,
        ILogger<DataKeyRotation> logger)
    {
        this.transactions = transactions;
        this.encryptor = encryptor;
        this.columns = columns.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        this.logger = logger;
    }

    public IReadOnlyList<EncryptedColumn> Columns => columns;

    /// <summary>What one run did and found, per column.</summary>
    /// <param name="KeyOverlap">this instance's current key is not the deployment's: nothing was tried</param>
    /// <param name="ColumnErrors">columns that could not be processed this run (logged; retried next run)</param>
    public sealed record Run(int CurrentKeyId, IReadOnlyList<ColumnRun> Columns, bool KeyOverlap, int ColumnErrors)
    {
        public long Reencrypted => Columns.Sum(c => c.Reencrypted);

        public long Failed => Columns.Sum(c => c.Failed);

        /// <summary>Values not under the current key after the run, in every column.</summary>
        public long Remaining => Columns.Sum(c => c.Remaining);

        public ColumnRun Column(string name) => Columns.First(c => c.Column == name);
    }

    /// <param name="RemainingByKeyId">values still not under the current key, by the key id they carry (0 for a value
    /// too short to carry one)</param>
    public sealed record ColumnRun(string Column, long Reencrypted, long Failed, IReadOnlyDictionary<int, long> RemainingByKeyId)
    {
        public long Remaining => RemainingByKeyId.Values.Sum();
    }

    private sealed record Row(Guid id, byte[] value);

    private sealed record Batched(int Rows, Guid Last, long Reencrypted, long Failed);

    public Task<Run> RunAsync(CancellationToken ct = default) => RunAsync(Batch, MaxBatchesPerRun, ct);

    public async Task<Run> RunAsync(int batchSize, int maxBatches, CancellationToken ct = default)
    {
        var current = encryptor.CurrentKeyId;
        var recorded = await transactions.RunAsync(PlatformScope,
            tx => tx.QuerySingleAsync<int>("select coalesce(data_key_current_id(), 0)", ct: ct), ct: ct);
        if (recorded != current)
        {
            // Every update would be refused (V20); this is a configuration overlap, not a failure of the values.
            logger.LogWarning("Data key rotation: this instance's current key {Current} is not the deployment's current key {Recorded} "
                + "(another instance started with another configuration); nothing re-encrypted this run", current, recorded);
            return new Run(current, [], true, 0);
        }
        var results = new List<ColumnRun>();
        var columnErrors = 0;
        foreach (var column in columns)
        {
            try
            {
                results.Add(await RotateAsync(column, current, batchSize, maxBatches, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // One column's trouble does not stop the others; it is retried on the next run.
                logger.LogError(ex, "Data key rotation: {Column} could not be processed this run", column.Name);
                columnErrors++;
            }
        }
        var run = new Run(current, results, false, columnErrors);
        Report(run);
        return run;
    }

    private async Task<ColumnRun> RotateAsync(EncryptedColumn column, int current, int batchSize, int maxBatches, CancellationToken ct)
    {
        var remaining = await RemainingAsync(column, current, ct);
        if (!remaining.Keys.Any(id => id > 0 && id < current))
        {
            return new ColumnRun(column.Name, 0, 0, remaining);
        }
        long reencrypted = 0;
        long failed = 0;
        var after = Guid.Empty;
        for (var i = 0; i < maxBatches; i++)
        {
            var from = after;
            var batch = await transactions.RunAsync(PlatformScope, tx => BatchAsync(tx, column, current, from, batchSize, ct), ct: ct);
            reencrypted += batch.Reencrypted;
            failed += batch.Failed;
            if (batch.Rows < batchSize)
            {
                break;
            }
            after = batch.Last;
        }
        return new ColumnRun(column.Name, reencrypted, failed, await RemainingAsync(column, current, ct));
    }

    private async Task<Batched> BatchAsync(DbTx tx, EncryptedColumn column, int current, Guid after, int limit, CancellationToken ct)
    {
        // Walks the primary key from where the last batch stopped, so a value that fails is passed over for the rest
        // of this run instead of being picked up by every batch.
        var rows = (await tx.QueryAsync<Row>($"""
            select id, {column.Column} as value from {column.Table}
            where id > @after and {column.Column} is not null and data_key_id({column.Column}) < @current
            order by id limit @limit
            for no key update skip locked
            """, new { after, current, limit }, ct)).ToList();
        long reencrypted = 0;
        long failed = 0;
        foreach (var row in rows)
        {
            byte[] next;
            try
            {
                next = encryptor.Reencrypt(row.value, column.Binding(row.id));
            }
            catch (ArgumentException)
            {
                failed++; // a key this instance does not hold, or a value that does not verify: left as it is
                continue;
            }
            try
            {
                // A savepoint per row: a refusal by the database (V20 guard) costs that row, not the batch.
                // Compare-and-set on the old value as well as the lock: a value is never re-encrypted twice.
                reencrypted += await tx.SavepointAsync("rekey", () => tx.ExecuteAsync(
                    $"update {column.Table} set {column.Column} = @next where id = @id and {column.Column} = @previous",
                    new { next, id = row.id, previous = row.value }, ct), ct);
            }
            catch (NpgsqlException)
            {
                failed++;
            }
        }
        return new Batched(rows.Count, rows.Count == 0 ? after : rows[^1].id, reencrypted, failed);
    }

    /// <summary>Values not under the current key, by key id. One scan of the column; these tables are small.</summary>
    private async Task<IReadOnlyDictionary<int, long>> RemainingAsync(EncryptedColumn column, int current, CancellationToken ct)
    {
        var rows = await transactions.RunAsync(PlatformScope, tx => tx.QueryAsync<(int KeyId, long N)>($"""
            select coalesce(data_key_id({column.Column}), 0) as key_id, count(*) as n from {column.Table}
            where {column.Column} is not null and data_key_id({column.Column}) is distinct from @current
            group by 1
            """, new { current }, ct), ct: ct);
        return new SortedDictionary<int, long>(rows.ToDictionary(r => r.KeyId, r => r.N));
    }

    private void Report(Run run)
    {
        if (run.Remaining == 0 && run.Reencrypted == 0 && run.Failed == 0)
        {
            // Said once per settled period, so a deploy's log shows it without repeating every run.
            if (Interlocked.Exchange(ref settledReported, 1) == 0)
            {
                logger.LogInformation("Data key rotation: every encrypted value is under the current key {Current}", run.CurrentKeyId);
            }
            return;
        }
        Interlocked.Exchange(ref settledReported, 0);
        var detail = string.Join("; ", run.Columns.Where(c => c.Remaining > 0 || c.Failed > 0)
            .Select(c => c.Column + " " + string.Join(",", c.RemainingByKeyId.Select(e => $"key {e.Key}={e.Value}"))
                + (c.Failed > 0 ? $" failed={c.Failed}" : "")));
        var unreadable = run.Columns.SelectMany(c => c.RemainingByKeyId.Keys).Any(id => !encryptor.KnowsKey(id));
        if (unreadable || run.Failed > 0)
        {
            logger.LogError("Data key rotation: current key {Current}; re-encrypted {Reencrypted}, failed {Failed}; {Remaining} values not under "
                + "the current key, some under a key this instance does not hold or not readable [{Detail}]", run.CurrentKeyId,
                run.Reencrypted, run.Failed, run.Remaining, detail);
        }
        else
        {
            logger.LogInformation("Data key rotation: current key {Current}; re-encrypted {Reencrypted}, failed 0; {Remaining} values not "
                + "under the current key [{Detail}]", run.CurrentKeyId, run.Reencrypted, run.Remaining, detail);
        }
    }
}

/// <summary>
/// The schedule of <see cref="DataKeyRotation"/> (Java <c>DataKeyRotation.scheduled</c>): every 10 minutes, first 2
/// minutes after start (<c>Zawed:DataKeyRotation:Interval</c>, <c>:InitialDelay</c>). Registered only when
/// <c>Zawed:DataKeyRotation:Enabled</c> is not false, so a paused job is not expected by the health check.
/// <para>Recorded in <c>job_runs</c>, counts only. An instance whose current key is not the deployment's does nothing:
/// that is recorded as a skip, so a configuration overlap that lasts turns the job's health DOWN instead of passing for
/// a success. A run in which every column failed is recorded as a failure.</para>
/// </summary>
public sealed class DataKeyRotationJob : ScheduledJob
{
    public const string EnabledKey = "Zawed:DataKeyRotation:Enabled";

    private readonly DataKeyRotation rotation;

    public DataKeyRotationJob(Jobs.Jobs jobs, DataKeyRotation rotation, IConfiguration configuration)
        // Every 10 minutes: an hour without a success is several missed runs.
        : base(jobs, "data-key-rotation", TimeSpan.FromHours(1))
    {
        this.rotation = rotation;
        InitialDelay = Durations.Get(configuration, "Zawed:DataKeyRotation:InitialDelay", TimeSpan.FromMinutes(2));
        Interval = Durations.Get(configuration, "Zawed:DataKeyRotation:Interval", TimeSpan.FromMinutes(10));
    }

    public static bool Enabled(IConfiguration configuration) =>
        !string.Equals(configuration[EnabledKey], "false", StringComparison.OrdinalIgnoreCase);

    protected override TimeSpan InitialDelay { get; }

    protected override TimeSpan Interval { get; }

    protected override async Task<JobResult> RunOnceAsync(CancellationToken ct)
    {
        var run = await rotation.RunAsync(ct);
        if (run.KeyOverlap)
        {
            return JobResult.SkippedLocked();
        }
        if (rotation.Columns.Count > 0 && run.ColumnErrors == rotation.Columns.Count)
        {
            // Every column failed (each already logged with its cause): the run did nothing.
            throw new InvalidOperationException("Data key rotation: no encrypted column could be processed");
        }
        return JobResult.Success(new Dictionary<string, long>
        {
            ["reencrypted"] = run.Reencrypted, ["failed"] = run.Failed, ["remaining"] = run.Remaining, ["columnErrors"] = run.ColumnErrors,
        });
    }
}
