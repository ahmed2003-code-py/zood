using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zawed.Api.Platform.Persistence.Migrations;
using Zawed.Tests.Support;

namespace Zawed.Tests.Modules.Identity;

/// <summary>A database migrated by the .NET runner as an ordinary owner: no superuser, no BYPASSRLS.</summary>
public sealed class OrdinaryOwnerDatabase : IAsyncLifetime
{
    public const string OwnerRole = "zawed_owner_test";
    private const string OwnerPassword = "owner-test-password";

    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var database = "owner_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(await TestDatabase.OwnerConnectionStringAsync("postgres")))
        {
            await admin.OpenAsync();
            await admin.ExecuteAsync($"""
                DO $$
                BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{OwnerRole}') THEN
                    CREATE ROLE {OwnerRole} LOGIN PASSWORD '{OwnerPassword}' NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
                  END IF;
                  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'zawed_app') THEN
                    CREATE ROLE zawed_app NOLOGIN;
                  END IF;
                END
                $$
                """);
            // What a managed database's administrator does once: the runtime role exists and the owner may hand it to
            // itself (V3: GRANT zawed_app TO CURRENT_USER).
            await admin.ExecuteAsync($"GRANT zawed_app TO {OwnerRole} WITH ADMIN OPTION");
            await admin.ExecuteAsync($"CREATE DATABASE {database} OWNER {OwnerRole}");
        }
        ConnectionString = new NpgsqlConnectionStringBuilder(await TestDatabase.OwnerConnectionStringAsync(database))
        {
            Username = OwnerRole,
            Password = OwnerPassword,
        }.ConnectionString;
        await new MigrationRunner(ConnectionString, MigrationRunner.Embedded(), NullLogger.Instance).MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

/// <summary>
/// The migrations and the sign-in path with an ordinary database owner (Java <c>NonSuperuserOwnerTests</c>). Railway and
/// the main test database connect as a superuser, which skips row-level security entirely, so only this test shows that
/// the migrations, applied by the .NET runner, and the SECURITY DEFINER functions work on a managed database where the
/// owner is a plain role (V5).
/// </summary>
public sealed class NonSuperuserOwnerTests(OrdinaryOwnerDatabase db) : IClassFixture<OrdinaryOwnerDatabase>
{
    [Fact]
    public async Task TheRuntimeRoleIsSubjectToRowLevelSecurityOnEveryScopedTable()
    {
        await using var connection = await OwnerAsync();
        await using var tx = await AsRuntimeRoleAsync(connection);
        Assert.False(await connection.QuerySingleAsync<bool>("select rolsuper or rolbypassrls from pg_roles where rolname = session_user", transaction: tx));
        // Without a scope the application's role sees no user, even one that exists.
        await BootstrapAsync(connection, tx, "rls-check@zawed.test");
        Assert.Equal(0L, await connection.QuerySingleAsync<long>("select count(*) from users", transaction: tx));
        // Row-level security is on for every scoped table, and nothing is left forced on the owner.
        Assert.Equal(19L, await connection.QuerySingleAsync<long>("""
            select count(*) from pg_class
            where relname in ('users', 'audit_log', 'companies', 'departments', 'vehicles', 'rfid_tags',
                              'limit_rules', 'station_operators', 'stations', 'pos_devices', 'agents',
                              'fuel_prices', 'ledger_accounts', 'ledger_transactions', 'ledger_entries',
                              'company_wallets', 'topup_requests', 'reconciliation_runs',
                              'reconciliation_findings')
              and relrowsecurity and not relforcerowsecurity
            """, transaction: tx));
    }

    [Fact]
    public async Task SignInFunctionsWorkWithoutAScope()
    {
        await using var connection = await OwnerAsync();
        await using var tx = await AsRuntimeRoleAsync(connection);
        var id = await BootstrapAsync(connection, tx, "first@zawed.test");

        var found = await connection.QuerySingleOrDefaultAsync<Guid?>("select id from auth_login_lookup(@email)",
            new { email = "first@zawed.test" }, tx);
        Assert.Equal(id, found); // the login lookup finds the user
        Assert.True(await connection.QuerySingleAsync<bool>("select auth_super_admin_exists()", transaction: tx));

        await connection.ExecuteAsync(
            "select auth_record_login_failure(@id, true, 'WRONG_PASSWORD', 5, 900, now(), @audit, @lockAudit, 'req', '127.0.0.0/24')",
            new { id, audit = Guid.CreateVersion7(), lockAudit = Guid.CreateVersion7() }, tx);
        // The function's platform scope ended with it: the caller still has none.
        Assert.Equal("none", await connection.QuerySingleAsync<string>("select coalesce(app_scope_type(), 'none')", transaction: tx));
        Assert.Equal(0L, await connection.QuerySingleAsync<long>("select count(*) from users", transaction: tx));

        await connection.ExecuteAsync("select set_config('app.scope_type', 'PLATFORM', true)", transaction: tx);
        Assert.Equal(1, await connection.QuerySingleAsync<int>("select failed_login_count from users where email = 'first@zawed.test'", transaction: tx));
        Assert.Equal(1L, await connection.QuerySingleAsync<long>("select count(*) from audit_log where action = 'LOGIN_FAILED'", transaction: tx));
    }

    [Fact]
    public async Task AScopedCallerStillCannotWriteOutsideItsScope()
    {
        await using var connection = await OwnerAsync();
        await using var tx = await AsRuntimeRoleAsync(connection);
        await connection.ExecuteAsync("select set_config('app.scope_type', 'COMPANY', true), set_config('app.scope_id', @s, true)",
            new { s = Guid.CreateVersion7().ToString() }, tx);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync("""
            insert into companies (id, legal_name, name_ar, name_en, status, cooldown_minutes,
                                   low_balance_threshold_piastres, created_at, updated_at)
            values (@id, 'X', 'X', 'X', 'ACTIVE', 180, 0, now(), now())
            """, new { id = Guid.CreateVersion7() }, tx));
        Assert.Contains("row-level security", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>U1 (V22): the demo form's door and the shared rate limiter are SECURITY DEFINER functions too.</summary>
    [Fact]
    public async Task TheDemoFormAndTheSharedLimiterWorkWithoutAScope()
    {
        await using var connection = await OwnerAsync();
        await using var tx = await AsRuntimeRoleAsync(connection);
        var reference = await connection.QuerySingleAsync<string>(
            "select lead_reference from submit_lead(@id, 'Owner Test Co', 'Mona', '01001234567', 'o@example.test', '1-19', 'ar')",
            new { id = Guid.CreateVersion7() }, tx);
        Assert.Matches("^L-[0-9]{5,}$", reference);
        Assert.Equal(0L, await connection.QuerySingleAsync<long>("select count(*) from leads", transaction: tx));
        var key = new string('a', 64);
        Assert.Equal(1, await connection.QuerySingleAsync<int>("select hit_count from rate_limit_hit('owner-test', @key, 60)", new { key }, tx));
        Assert.Equal(2, await connection.QuerySingleAsync<int>("select hit_count from rate_limit_hit('owner-test', @key, 60)", new { key }, tx));
        Assert.Equal(0, await connection.QuerySingleAsync<int>("select purge_rate_limit_counters(100)", transaction: tx));
    }

    private async Task<NpgsqlConnection> OwnerAsync()
    {
        var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>A transaction as the application runs one: the owner, switched to zawed_app.</summary>
    private static async Task<NpgsqlTransaction> AsRuntimeRoleAsync(NpgsqlConnection connection)
    {
        var tx = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync("SET LOCAL ROLE zawed_app", transaction: tx);
        return tx;
    }

    /// <summary>Creates the first Super Admin through the bootstrap function, with no scope set.</summary>
    private static async Task<Guid> BootstrapAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string email)
    {
        var id = Guid.CreateVersion7();
        Assert.True(await connection.QuerySingleAsync<bool>(
            "select auth_bootstrap_super_admin(@id, @email, 'First', 'hash', now(), @audit, 'req', '127.0.0.0/24')",
            new { id, email, audit = Guid.CreateVersion7() }, tx)); // bootstrap created the user
        return id;
    }
}
