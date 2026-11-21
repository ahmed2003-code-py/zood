using System.Data;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Npgsql;

namespace Zawed.Api.Platform.Persistence;

/// <summary>
/// One database transaction that has told PostgreSQL its scope (Java <c>ScopedTransactionManager</c>): the
/// connection, the transaction, the scope, and the explicit-SQL helpers the money path uses. EF Core
/// shares the same connection and transaction through <see cref="Db"/>, so an EF write and a Dapper
/// statement in one request commit or roll back together.
/// <para>The three settings are transaction-local (<c>set_config(..., true)</c>): they vanish at commit or
/// rollback, so a pooled connection never carries one request's scope into the next. In the same round
/// trip the transaction checks it runs as <c>zawed_app</c>, the only role row-level security binds (V3,
/// V5), and refuses to continue otherwise.</para>
/// </summary>
public sealed partial class DbTx : IAsyncDisposable
{
    public const string RuntimeRole = "zawed_app";

    private const string SetScope = "select set_config('app.scope_type', @type, true), set_config('app.scope_id', @id, true), "
        + "set_config('app.user_id', @user, true), current_user";

    private readonly List<Func<Task>> afterCommit = [];
    private ZawedDbContext? db;
    private bool finished;
    private int savepoints;

    private DbTx(NpgsqlConnection connection, NpgsqlTransaction transaction, DbScopeContext scope)
    {
        Connection = connection;
        Transaction = transaction;
        Scope = scope;
    }

    public NpgsqlConnection Connection { get; }
    public NpgsqlTransaction Transaction { get; }
    public DbScopeContext Scope { get; }
    public bool IsFinished => finished;

    /// <summary>Opens a connection from the runtime data source, begins READ COMMITTED and sets the scope.</summary>
    internal static async Task<DbTx> BeginAsync(NpgsqlDataSource dataSource, DbScopeContext scope, CancellationToken ct)
    {
        var connection = await dataSource.OpenConnectionAsync(ct);
        try
        {
            var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var role = await connection.QuerySingleAsync<(string, string, string, string)>(new CommandDefinition(SetScope, new
            {
                type = scope.Scope?.Type.ToString() ?? "",
                id = scope.Scope?.Id?.ToString() ?? "",
                user = scope.UserId?.ToString() ?? "",
            }, transaction, cancellationToken: ct));
            if (role.Item4 != RuntimeRole)
            {
                throw new InvalidOperationException($"Refusing a transaction as database role '{role.Item4}': row-level "
                    + $"security binds only {RuntimeRole} (check the runtime data source's SET ROLE)");
            }
            return new DbTx(connection, transaction, scope);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>The EF Core context bound to this transaction, created on first use.</summary>
    public ZawedDbContext Db() => db ??= ZawedDbContext.Create(this);

    /// <summary>Work to run after a successful commit only (notifications, cache eviction; playbook outbox).</summary>
    public void AfterCommit(Func<Task> work) => afterCommit.Add(work);

    public Task<int> ExecuteAsync(string sql, object? args = null, CancellationToken ct = default) =>
        Connection.ExecuteAsync(new CommandDefinition(sql, args, Transaction, cancellationToken: ct));

    public Task<IEnumerable<T>> QueryAsync<T>(string sql, object? args = null, CancellationToken ct = default) =>
        Connection.QueryAsync<T>(new CommandDefinition(sql, args, Transaction, cancellationToken: ct));

    public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? args = null, CancellationToken ct = default) =>
        Connection.QuerySingleOrDefaultAsync<T>(new CommandDefinition(sql, args, Transaction, cancellationToken: ct));

    public Task<T> QuerySingleAsync<T>(string sql, object? args = null, CancellationToken ct = default) =>
        Connection.QuerySingleAsync<T>(new CommandDefinition(sql, args, Transaction, cancellationToken: ct));

    /// <summary>
    /// <c>pg_advisory_xact_lock</c>: the playbook's <c>sp_getapplock</c> for PostgreSQL. Serialises work on a
    /// logical resource for which no single row can be locked; released at commit or rollback.
    /// </summary>
    public Task AdvisoryLockAsync(string resource, CancellationToken ct = default) =>
        ExecuteAsync("select pg_advisory_xact_lock(hashtextextended(@resource, 0))", new { resource }, ct);

    /// <summary><c>pg_try_advisory_xact_lock</c>: false when another transaction holds it (a job that should skip).</summary>
    public Task<bool> TryAdvisoryLockAsync(string resource, CancellationToken ct = default) =>
        QuerySingleAsync<bool>("select pg_try_advisory_xact_lock(hashtextextended(@resource, 0))", new { resource }, ct);

    /// <summary>
    /// Runs <paramref name="work"/> inside a savepoint: a failure of the work rolls back only the savepoint and is
    /// rethrown as it is, so the caller decides whether the outer transaction goes on (the notification pattern of B7 W5:
    /// a notification that cannot be written must not stop a fueling).
    /// <para>A failure of the savepoint itself (<c>SAVEPOINT</c>, <c>RELEASE</c> or <c>ROLLBACK TO</c>, for example in a
    /// transaction an earlier statement already aborted, or on a broken connection) is a <see cref="SavepointFailedException"/>:
    /// the outer transaction can no longer be trusted, and no caller may swallow it.</para>
    /// <para>EF Core (wave 5B hardening). The tracker is not part of the database transaction, so the savepoint must not
    /// leave it ahead of the database. Before <c>SAVEPOINT</c> it refuses unsaved changes of the cause
    /// (<see cref="SavepointFailedException"/>): they would either be lost by a rollback or be saved by the work inside
    /// the savepoint and rolled back with it. Refusing is safer than saving them here: a save at this point would move
    /// the cause's constraint failures into its handler, and the convention is already that a service saves before it
    /// publishes, so a handler sees the row. On a rollback, entries the work started tracking are detached, and the
    /// entries the cause tracked before are restored to the values they had (which, with nothing pending, are the
    /// database's), so a later save by the cause still writes what it changes.</para>
    /// </summary>
    public async Task<T> SavepointAsync<T>(string name, Func<Task<T>> work, CancellationToken ct = default)
    {
        if (db is not null && db.ChangeTracker.HasChanges())
        {
            throw new SavepointFailedException($"Savepoint {name} refused: the EF Core context has unsaved changes. Save them "
                + "before publishing an event or opening a savepoint, or a rollback to the savepoint would lose them.");
        }
        if (!SavepointName().IsMatch(name))
        {
            throw new ArgumentException($"Not a plain savepoint name: {name}", nameof(name));
        }
        // Unique within the transaction: PostgreSQL allows a name again, and ROLLBACK TO would then land on an outer
        // savepoint of that name when this one was never created, silently undoing the outer work as well.
        name = $"{name}_{++savepoints}";
        var tracked = TrackedSnapshot();
        await Machinery("savepoint " + name, ct);
        T result;
        try
        {
            result = await work();
        }
        catch
        {
            await Machinery("rollback to savepoint " + name, ct);
            RestoreTracked(tracked);
            throw;
        }
        await Machinery("release savepoint " + name, ct);
        return result;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z_][a-z0-9_]{0,40}$")]
    private static partial System.Text.RegularExpressions.Regex SavepointName();

    /// <summary>
    /// A savepoint statement, sent at once as plain SQL; its failure (other than a requested cancellation) is a
    /// <see cref="SavepointFailedException"/>. Not <c>NpgsqlTransaction.SaveAsync</c>: Npgsql defers that <c>SAVEPOINT</c>
    /// to the next command, and when it fails there (an aborted transaction) Npgsql ends the whole transaction, so the
    /// cause would go on with a disposed transaction while PostgreSQL had already rolled everything back (measured in
    /// <c>DbTxTests</c>). Sent at once, a <c>SAVEPOINT</c> in an aborted transaction fails before the work runs.
    /// </summary>
    private async Task Machinery(string statement, CancellationToken ct)
    {
        try
        {
            await ExecuteAsync(statement, ct: ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            throw new SavepointFailedException($"{statement} failed: the outer transaction can no longer be trusted", ex);
        }
    }

    /// <summary>The entities tracked before a savepoint, with their values (none pending: see <see cref="SavepointAsync{T}"/>).</summary>
    private Dictionary<object, PropertyValues>? TrackedSnapshot() =>
        db?.ChangeTracker.Entries().ToDictionary(e => e.Entity, e => e.CurrentValues.Clone(), ReferenceEqualityComparer.Instance);

    /// <summary>
    /// After a rollback to the savepoint: detaches what the work started tracking, then puts every entity tracked before
    /// back as it was (values and state), re-attaching one the work deleted or detached.
    /// </summary>
    private void RestoreTracked(Dictionary<object, PropertyValues>? before)
    {
        if (db is null)
        {
            return;
        }
        if (before is null || before.Count == 0)
        {
            db.ChangeTracker.Clear();
            return;
        }
        foreach (var entry in db.ChangeTracker.Entries().ToList())
        {
            if (!before.ContainsKey(entry.Entity))
            {
                entry.State = EntityState.Detached;
            }
        }
        foreach (var (entity, values) in before)
        {
            var entry = db.Entry(entity);
            if (entry.State == EntityState.Detached)
            {
                entry.State = EntityState.Unchanged;
            }
            entry.CurrentValues.SetValues(values);
            entry.OriginalValues.SetValues(values);
            entry.State = EntityState.Unchanged;
        }
    }

    /// <summary>
    /// Saves what EF Core still tracks, then commits. PostgreSQL answers <c>COMMIT</c> in a transaction an earlier
    /// statement aborted with <c>ROLLBACK</c> and no error, and Npgsql 10 reports that as a success (measured in
    /// <c>DbTxTests</c>), so a caller that caught a failed statement and went on would be told its work committed when
    /// none of it did. The commit is therefore sent as a batch behind a probe (<c>select 1</c>): one round trip, and in an
    /// aborted transaction the probe fails with 25P02, PostgreSQL skips the rest of the batch, and this throws a
    /// <see cref="TransactionAbortedException"/> with the transaction still open, for the caller's rollback.
    /// </summary>
    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (db is not null && db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct);
        }
        var commit = new NpgsqlBatchCommand("commit");
        await using (var batch = new NpgsqlBatch(Connection, Transaction) { BatchCommands = { new NpgsqlBatchCommand("select 1"), commit } })
        {
            try
            {
                await batch.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InFailedSqlTransaction && ex.BatchCommand != commit)
            {
                throw new TransactionAbortedException(ex);
            }
            catch (PostgresException ex) when (ex.BatchCommand == commit)
            {
                // COMMIT itself failed (a deferred constraint, a serialization failure): PostgreSQL has ended the
                // transaction, so there is nothing left to roll back.
                finished = true;
                throw;
            }
        }
        finished = true;
        foreach (var work in afterCommit)
        {
            await work();
        }
        afterCommit.Clear();
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        afterCommit.Clear();
        if (!finished)
        {
            finished = true;
            await Transaction.RollbackAsync(ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!finished)
        {
            try
            {
                await RollbackAsync();
            }
            catch (Exception) when (Connection.State != ConnectionState.Open)
            {
                // The connection is already broken; disposing it below is all that is left.
            }
        }
        if (db is not null)
        {
            await db.DisposeAsync();
        }
        await Transaction.DisposeAsync();
        await Connection.DisposeAsync();
    }
}

/// <summary>
/// A savepoint could not be opened, released or rolled back to, or was refused because EF Core held unsaved changes
/// (<see cref="DbTx.SavepointAsync{T}"/>). Unlike a failure of the work inside the savepoint, it says the outer transaction
/// is no longer in a known state: it must fail its cause, and no handler may swallow it.
/// </summary>
public sealed class SavepointFailedException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>
/// A commit was asked for a transaction that an earlier failed statement had aborted (SQLSTATE 25P02): PostgreSQL would
/// have rolled it back and answered as if it committed. Nothing of the transaction was written.
/// </summary>
public sealed class TransactionAbortedException(PostgresException inner) : InvalidOperationException(
    "The transaction was aborted by an earlier failed statement and was not committed: PostgreSQL would roll it back", inner);
