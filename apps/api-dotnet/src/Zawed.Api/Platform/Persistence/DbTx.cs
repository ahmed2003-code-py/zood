using System.Data;
using Dapper;
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
public sealed class DbTx : IAsyncDisposable
{
    public const string RuntimeRole = "zawed_app";

    private const string SetScope = "select set_config('app.scope_type', @type, true), set_config('app.scope_id', @id, true), "
        + "set_config('app.user_id', @user, true), current_user";

    private readonly List<Func<Task>> afterCommit = [];
    private ZawedDbContext? db;
    private bool finished;

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
    /// Runs <paramref name="work"/> inside a savepoint: a failure rolls back only the savepoint and is
    /// rethrown, so the caller decides whether the outer transaction goes on (the notification pattern of
    /// B7 W5: a notification that cannot be written must not stop a fueling).
    /// </summary>
    public async Task<T> SavepointAsync<T>(string name, Func<Task<T>> work, CancellationToken ct = default)
    {
        await Transaction.SaveAsync(name, ct);
        try
        {
            var result = await work();
            await Transaction.ReleaseAsync(name, ct);
            return result;
        }
        catch
        {
            await Transaction.RollbackAsync(name, ct);
            db?.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (db is not null && db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct);
        }
        await Transaction.CommitAsync(ct);
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
