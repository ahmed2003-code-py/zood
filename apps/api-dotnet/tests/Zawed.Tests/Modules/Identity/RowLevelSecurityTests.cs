using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Support;

namespace Zawed.Tests.Modules.Identity;

/// <summary>
/// Isolation at the database layer for users and the audit trail (plan §3.6, PRD §9; Java <c>RowLevelSecurityTests</c>):
/// even a hand-written query, run the way the application runs (its pooled data source, role zawed_app), sees only its
/// own scope's rows, cannot write into another scope, and cannot change the audit trail. The other tables' rows come
/// with their modules (wave 5A).
/// </summary>
public sealed class RowLevelSecurityTests(SharedApi api) : IClassFixture<SharedApi>
{
    private readonly IdentityTestSupport id = new(api.Factory);

    private NpgsqlDataSource DataSource => api.Factory.Services.GetRequiredService<NpgsqlDataSource>();

    [Fact]
    public async Task TheApplicationNeverRunsAsTheOwner()
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        var (user, super) = await connection.QuerySingleAsync<(string, bool)>(
            "select current_user::text, (select rolsuper from pg_roles where rolname = current_user)");
        Assert.Equal("zawed_app", user);
        Assert.False(super);
    }

    [Fact]
    public async Task ACompanyScopeSeesOnlyItsOwnUsers()
    {
        var companyA = Scope.Company(Guid.CreateVersion7());
        var companyB = Scope.Company(Guid.CreateVersion7());
        var a = await id.SeedAsync(Role.FLEET_MANAGER, companyA, false);
        var b = await id.SeedAsync(Role.ACCOUNTANT, companyB, false);

        await using var scoped = await ScopedAsync(companyA);
        Assert.Equal(1L, await Count(scoped, "select count(*) from users where id = @p", a.Id));
        // Asked for by id, filtered by nothing: still invisible.
        Assert.Equal(0L, await Count(scoped, "select count(*) from users where id = @p", b.Id));
        Assert.Equal(0L, await Count(scoped, "select count(*) from users where scope_id = @p", companyB.Id!.Value));
    }

    [Fact]
    public async Task WithoutAScopeNoUserIsVisible()
    {
        var user = await id.SeedAsync(Role.FLEET_MANAGER);
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        Assert.Equal(0L, await connection.QuerySingleAsync<long>("select count(*) from users where id = @p", new { p = user.Id }, tx));
    }

    [Fact]
    public async Task ACompanyScopeCannotMoveAUserIntoAnotherCompany()
    {
        var companyA = Scope.Company(Guid.CreateVersion7());
        var a = await id.SeedAsync(Role.FLEET_MANAGER, companyA, false);
        await using var scoped = await ScopedAsync(companyA);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => scoped.Connection.ExecuteAsync(
            "update users set scope_id = @s where id = @id", new { s = Guid.CreateVersion7(), id = a.Id }, scoped.Transaction));
        Assert.Contains("row-level security", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAuditTrailCannotBeChangedOrDeleted()
    {
        await id.SeedAsync(Role.FLEET_MANAGER);
        foreach (var sql in new[] { "update audit_log set action = 'X'", "delete from audit_log" })
        {
            await using var scoped = await ScopedAsync(Scope.Platform);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => scoped.Connection.ExecuteAsync(sql, transaction: scoped.Transaction));
            Assert.Contains("permission denied", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task UsersCannotBeDeletedOnlyDeactivated()
    {
        await using var scoped = await ScopedAsync(Scope.Platform);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => scoped.Connection.ExecuteAsync("delete from users", transaction: scoped.Transaction));
        Assert.Contains("permission denied", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheScopeDoesNotLeakIntoTheNextTransactionOnThePooledConnection()
    {
        var user = await id.SeedAsync(Role.FLEET_MANAGER);
        await using var scoped = await ScopedAsync(Scope.Platform);
        Assert.Equal(1L, await Count(scoped, "select count(*) from users where id = @p", user.Id));
        await scoped.Transaction.CommitAsync();
        // A new transaction on the same connection: the scope was transaction-local.
        await using var next = await scoped.Connection.BeginTransactionAsync();
        Assert.Equal(0L, await scoped.Connection.QuerySingleAsync<long>("select count(*) from users where id = @p", new { p = user.Id }, next));
    }

    [Fact]
    public async Task AScopedReaderOfTheAuditLogSeesOnlyRowsAboutItsOwnScope()
    {
        // audit_log_read (V3): a company reads what was done to its own users, nothing else.
        var companyA = Scope.Company(Guid.CreateVersion7());
        var companyB = Scope.Company(Guid.CreateVersion7());
        foreach (var (scope, entity) in new[] { (companyA, Guid.CreateVersion7()), (companyB, Guid.CreateVersion7()) })
        {
            await id.Owner.ExecuteAsync("""
                insert into audit_log (id, occurred_at, action, entity_type, entity_id, scope_type, scope_id)
                values (@id, now(), 'RLS_PROBE', 'USER', @entity, 'COMPANY', @scope)
                """, new { id = Guid.CreateVersion7(), entity, scope = scope.Id });
        }
        await using var scoped = await ScopedAsync(companyA);
        Assert.Equal(1L, await Count(scoped, "select count(*) from audit_log where action = 'RLS_PROBE' and scope_id = @p", companyA.Id!.Value));
        Assert.Equal(0L, await Count(scoped, "select count(*) from audit_log where action = 'RLS_PROBE' and scope_id = @p", companyB.Id!.Value));
        var ex = await Assert.ThrowsAsync<PostgresException>(() => scoped.Connection.ExecuteAsync("""
            insert into audit_log (id, occurred_at, action, entity_type, scope_type, scope_id)
            values (@id, now(), 'RLS_PROBE', 'USER', 'COMPANY', @scope)
            """, new { id = Guid.CreateVersion7(), scope = companyB.Id }, scoped.Transaction));
        Assert.Contains("row-level security", ex.Message, StringComparison.Ordinal);
    }

    private sealed class Scoped(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public NpgsqlConnection Connection { get; } = connection;
        public NpgsqlTransaction Transaction { get; } = transaction;

        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    /// <summary>A pooled connection inside a transaction with a scope set, the way <c>DbTx</c> does it.</summary>
    private async Task<Scoped> ScopedAsync(Scope scope)
    {
        var connection = await DataSource.OpenConnectionAsync();
        var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync("select set_config('app.scope_type', @t, true), set_config('app.scope_id', @s, true)",
            new { t = scope.Type.ToString(), s = scope.Id?.ToString() ?? "" }, transaction);
        return new Scoped(connection, transaction);
    }

    private static Task<long> Count(Scoped scoped, string sql, Guid parameter) =>
        scoped.Connection.QuerySingleAsync<long>(sql, new { p = parameter }, scoped.Transaction);
}
