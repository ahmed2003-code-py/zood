using Npgsql;
using Zawed.Api.Platform.Security;

namespace Zawed.Api.Platform.Persistence;

/// <summary>
/// Opens scoped transactions. The per-request one is opened by <see cref="TransactionFilter"/>; code that
/// needs its own (the Java <c>REQUIRES_NEW</c> and <c>TransactionTemplate</c> cases) uses
/// <see cref="RunAsync{T}"/>:
/// <list type="bullet">
/// <item>a write that must survive the request's refusal: lockout counters and failure audits at sign-in,
/// rate-limit counts;</item>
/// <item>work that is split into several transactions on purpose: one per offline-sync item, one per
/// import row, one per purge batch;</item>
/// <item>work that retries a whole transaction (a deadlock victim on the fueling path);</item>
/// <item>jobs, which have no request.</item>
/// </list>
/// </summary>
public interface ITransactions
{
    Task<DbTx> BeginAsync(DbScopeContext scope, CancellationToken ct = default);

    /// <summary>
    /// Runs <paramref name="work"/> in a new transaction and commits it; rolls back and rethrows on any
    /// exception. With <paramref name="retryOnConflict"/>, a deadlock or serialization failure reruns the
    /// whole transaction once (ADR 0018 §4: "a deadlock victim is still retried once").
    /// </summary>
    Task<T> RunAsync<T>(DbScopeContext scope, Func<DbTx, Task<T>> work, bool retryOnConflict = false, CancellationToken ct = default);
}

internal sealed class Transactions(NpgsqlDataSource dataSource) : ITransactions
{
    public Task<DbTx> BeginAsync(DbScopeContext scope, CancellationToken ct = default) => DbTx.BeginAsync(dataSource, scope, ct);

    public async Task<T> RunAsync<T>(DbScopeContext scope, Func<DbTx, Task<T>> work, bool retryOnConflict = false, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var tx = await BeginAsync(scope, ct);
            try
            {
                var result = await work(tx);
                await tx.CommitAsync(ct);
                return result;
            }
            catch (Exception ex) when (retryOnConflict && attempt == 1 && DbErrors.IsRetryableConflict(ex))
            {
                await tx.RollbackAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
    }
}

/// <summary>
/// The request's transaction, if the request has one (scoped service). Services take their
/// <see cref="DbTx"/> from here; outside a transaction <see cref="Current"/> throws, so a query can never
/// run without a scope by accident.
/// </summary>
public interface IDbSession
{
    DbTx Current { get; }
    bool HasTransaction { get; }
}

internal sealed class DbSession : IDbSession, IAsyncDisposable
{
    private DbTx? current;

    public DbTx Current => current ?? throw new InvalidOperationException(
        "No transaction: this code runs outside the per-request transaction. Use ITransactions.RunAsync, or remove "
        + "[OwnTransactions] from the action.");

    public bool HasTransaction => current is { IsFinished: false };

    internal void Attach(DbTx tx) => current = tx;

    internal void Detach() => current = null;

    public async ValueTask DisposeAsync()
    {
        if (current is not null)
        {
            await current.DisposeAsync();
            current = null;
        }
    }
}

/// <summary>PostgreSQL error classes the platform reacts to.</summary>
public static class DbErrors
{
    public const string UniqueViolation = "23505";
    public const string SerializationFailure = "40001";
    public const string DeadlockDetected = "40P01";
    public const string LockNotAvailable = "55P03";

    public static PostgresException? Postgres(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is PostgresException pg)
            {
                return pg;
            }
        }
        return null;
    }

    public static bool IsUniqueViolation(Exception ex, string? constraint = null) =>
        Postgres(ex) is { SqlState: UniqueViolation } pg && (constraint is null || pg.ConstraintName == constraint);

    public static bool IsRetryableConflict(Exception ex) =>
        Postgres(ex)?.SqlState is SerializationFailure or DeadlockDetected;
}

/// <summary>Who the request is, for the database: the signed-in user's scope, or none.</summary>
public static class RequestScope
{
    public static DbScopeContext Of(HttpContext context) => DbScopeContext.ForPrincipal(ZawedPrincipal.From(context.User));
}
