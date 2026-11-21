using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zawed.Api.Modules.Tenancy;
using Zawed.Api.Modules.Tenancy.Contracts;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Support;

namespace Zawed.Tests.Modules.Tenancy;

/// <summary>
/// Row-level security on the B2 tables, straight against the database as the runtime role: what a scope can see
/// and write whatever the application code does (Java <c>MasterDataIsolationTests</c>). Then the second layer, the
/// EF Core global query filter and write guard, and the <see cref="ICompanyDirectory"/> contract under a scope.
/// </summary>
public sealed class MasterDataIsolationTests(ProbeApi api) : IClassFixture<ProbeApi>
{
    private static readonly string[] CompanyTables = ["companies", "departments", "vehicles", "rfid_tags", "limit_rules"];
    private static readonly string[] OperatorTables = ["station_operators", "stations", "pos_devices", "agents"];

    [Fact]
    public async Task WithoutAScopeNothingIsVisible()
    {
        await CompanyAsync();
        await OperatorAsync();
        foreach (var table in CompanyTables.Concat(OperatorTables))
        {
            Assert.Equal(0, await AsAppAsync(null, null, c => CountAsync(c, table)));
        }
    }

    [Fact]
    public async Task ACompanySeesItsOwnRowsOnly()
    {
        var companyA = await CompanyAsync();
        var companyB = await CompanyAsync();
        await OperatorAsync();
        await AsAppAsync("COMPANY", companyA, async c =>
        {
            Assert.Equal(0, await CountAsync(c, $"companies where id = '{companyB}'"));
            Assert.Equal(1, await CountAsync(c, $"companies where id = '{companyA}'"));
            Assert.Equal(0, await CountAsync(c, $"vehicles where company_id = '{companyB}'"));
            Assert.Equal(1, await CountAsync(c, $"vehicles where company_id = '{companyA}'"));
            foreach (var table in OperatorTables)
            {
                Assert.Equal(0, await CountAsync(c, table));
            }
            return 0;
        });
    }

    [Fact]
    public async Task AnOperatorSeesItsOwnRowsOnly()
    {
        var operatorA = await OperatorAsync();
        var operatorB = await OperatorAsync();
        await CompanyAsync();
        await AsAppAsync("OPERATOR", operatorA, async c =>
        {
            Assert.Equal(0, await CountAsync(c, $"stations where operator_id = '{operatorB}'"));
            Assert.Equal(1, await CountAsync(c, $"stations where operator_id = '{operatorA}'"));
            foreach (var table in CompanyTables)
            {
                Assert.Equal(0, await CountAsync(c, table));
            }
            return 0;
        });
    }

    [Fact]
    public async Task AScopedUserCannotWriteMasterData()
    {
        var companyA = await CompanyAsync();
        var companyB = await CompanyAsync();
        var sneaky = await AsAppAsync("COMPANY", companyA, c => RefusedAsync(c, $"""
            insert into departments (id, company_id, name, status, created_at, updated_at)
            values ('{Guid.CreateVersion7()}', '{companyA}', 'Sneaky', 'ACTIVE', now(), now())
            """));
        Assert.Contains("row-level security", sneaky);
        // Its own rows are visible, but no change to them passes the policy's check.
        var ownRow = await AsAppAsync("COMPANY", companyA, c => RefusedAsync(c, $"update vehicles set tier = 'VIP' where company_id = '{companyA}'"));
        Assert.Contains("row-level security", ownRow);
        // Another company's rows are not even found.
        Assert.Equal(0, await AsAppAsync("COMPANY", companyA, c => c.ExecuteAsync($"update vehicles set make = 'X' where company_id = '{companyB}'")));
    }

    [Fact]
    public async Task TheDatabaseRefusesAChildFromAnotherTenant()
    {
        var companyA = await CompanyAsync();
        var companyB = await CompanyAsync();
        var departmentOfB = await api.Owner.QueryOneAsync<Guid>("select id from departments where company_id = @companyB", new { companyB });
        var refused = await AsAppAsync("PLATFORM", null, c => RefusedAsync(c,
            $"update vehicles set department_id = '{departmentOfB}' where company_id = '{companyA}'"));
        Assert.Contains("is not in company", refused);
    }

    [Fact]
    public async Task NothingCanBeDeletedAndPricesCannotBeEdited()
    {
        var companyA = await CompanyAsync();
        Assert.Contains("permission denied", await AsAppAsync("PLATFORM", null, c => RefusedAsync(c, $"delete from vehicles where company_id = '{companyA}'")));
        Assert.Contains("permission denied", await AsAppAsync("PLATFORM", null, c => RefusedAsync(c, "update fuel_prices set price_piastres = 1")));
    }

    // ------------------------------------------------------------------------------------------------------
    // The second layer: EF Core's filter and write guard, which make a mistake visible before RLS has to catch it.

    [Fact]
    public async Task TheEfFilterShowsACompanyItsOwnDepartmentsOnly()
    {
        var companyA = await CompanyAsync();
        var companyB = await CompanyAsync();
        var query = await InScopeAsync(Scope.Company(companyA), async (db, _) =>
        {
            var departments = db.Set<Department>().Select(d => d.CompanyId);
            Assert.Equal([companyA], await departments.Distinct().ToListAsync());
            Assert.Empty(await db.Set<Department>().Where(d => d.CompanyId == companyB).ToListAsync());
            return departments.ToQueryString();
        });
        // The filter is in the SQL EF sends, not only in the database's policy.
        Assert.Contains("company_id", query);
        Assert.Contains("ScopeCompanyId", query);
    }

    [Fact]
    public async Task TheEfWriteGuardRefusesAnotherCompanysRowBeforeTheDatabaseSeesIt()
    {
        var companyA = await CompanyAsync();
        var companyB = await CompanyAsync();
        var thrown = await InScopeAsync(Scope.Company(companyA), async (db, _) =>
        {
            db.Add(new Department
            {
                Id = Guid.CreateVersion7(), CompanyId = companyB, Name = "Sneaky", Status = DepartmentStatus.ACTIVE,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            return await Assert.ThrowsAsync<CrossScopeWriteException>(() => db.SaveChangesAsync());
        });
        Assert.Equal(nameof(Department), thrown.Entity);
    }

    [Fact]
    public async Task TheDirectoryAnswersWithinTheCallersScope()
    {
        var companyA = await CompanyAsync(lowBalanceThresholdPiastres: 7_000);
        var companyB = await CompanyAsync();
        var departmentOfA = await api.Owner.QueryOneAsync<Guid>("select id from departments where company_id = @companyA", new { companyA });
        var archived = Guid.CreateVersion7();
        await api.Owner.ExecuteAsync("""
            insert into departments (id, company_id, name, status, created_at, updated_at)
            values (@archived, @companyA, 'Old', 'ARCHIVED', now(), now())
            """, new { archived, companyA });

        await InScopeAsync(Scope.Company(companyA), async (_, directory) =>
        {
            var own = await directory.FindAsync(companyA);
            Assert.Equal(new CompanyRef(companyA, "شركة", "Co", true, 180, 7_000), own);
            Assert.Null(await directory.FindAsync(companyB));
            Assert.Equal([companyA], (await directory.FindAllAsync([companyA, companyB])).Select(c => c.Id));
            Assert.True(await directory.HasActiveDepartmentAsync(companyA, departmentOfA));
            Assert.False(await directory.HasActiveDepartmentAsync(companyA, archived));
            Assert.False(await directory.HasActiveDepartmentAsync(companyB, departmentOfA));
            return 0;
        });
        await InScopeAsync(Scope.Platform, async (_, directory) =>
        {
            Assert.Equal(2, (await directory.FindAllAsync([companyA, companyB, Guid.CreateVersion7()])).Count);
            Assert.Empty(await directory.FindAllAsync([]));
            return 0;
        });
    }

    // ----------------------------------------------------------------------------------------------- helpers

    /// <summary>Runs <paramref name="work"/> in an application transaction of <paramref name="scope"/>, as a request would.</summary>
    private async Task<T> InScopeAsync<T>(Scope scope, Func<ZawedDbContext, ICompanyDirectory, Task<T>> work)
    {
        await using var services = api.Factory.Services.CreateAsyncScope();
        var principal = new ZawedPrincipal(Guid.CreateVersion7(), Guid.CreateVersion7(), scope.IsPlatform ? Role.OPS : Role.FLEET_MANAGER, scope, false);
        var session = services.ServiceProvider.GetRequiredService<DbSession>();
        await using var tx = await services.ServiceProvider.GetRequiredService<ITransactions>().BeginAsync(DbScopeContext.ForPrincipal(principal));
        session.Attach(tx);
        try
        {
            return await work(tx.Db(), services.ServiceProvider.GetRequiredService<ICompanyDirectory>());
        }
        finally
        {
            session.Detach();
        }
    }

    /// <summary>A connection as the application has it: the runtime role, in a transaction with a scope.</summary>
    private Task<T> AsAppAsync<T>(string? scopeType, Guid? scopeId, Func<NpgsqlConnection, Task<T>> work) =>
        api.Owner.AsRuntimeRoleAsync(scopeType, scopeId, null, (connection, _) => work(connection));

    private static async Task<long> CountAsync(NpgsqlConnection connection, string from) =>
        await connection.QuerySingleAsync<long>("select count(*) from " + from);

    private static async Task<string> RefusedAsync(NpgsqlConnection connection, string sql) =>
        (await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(sql))).MessageText;

    /// <summary>A company with one department and one vehicle (the Java helper of the same name).</summary>
    private async Task<Guid> CompanyAsync(long lowBalanceThresholdPiastres = 0)
    {
        var id = Guid.CreateVersion7();
        var department = Guid.CreateVersion7();
        var vehicle = Guid.CreateVersion7();
        var plate = "ISO " + vehicle.ToString("N")[^8..];
        await api.Owner.ExecuteAsync("""
            insert into companies (id, legal_name, name_ar, name_en, status, cooldown_minutes, low_balance_threshold_piastres, created_at, updated_at)
            values (@id, 'Co', 'شركة', 'Co', 'ACTIVE', 180, @lowBalanceThresholdPiastres, now(), now());
            insert into departments (id, company_id, name, status, created_at, updated_at)
            values (@department, @id, 'Dept', 'ACTIVE', now(), now());
            insert into vehicles (id, company_id, department_id, plate, plate_key, make, model, fuel_type, tank_capacity_ml, tier, status, created_at, updated_at)
            values (@vehicle, @id, @department, @plate, @plateKey, 'M', 'M', 'GAS_92', 50000, 'NORMAL', 'ACTIVE', now(), now());
            """, new { id, department, vehicle, plate, plateKey = plate.Replace(" ", "").ToUpperInvariant(), lowBalanceThresholdPiastres });
        return id;
    }

    /// <summary>An operator with one station.</summary>
    private async Task<Guid> OperatorAsync()
    {
        var id = Guid.CreateVersion7();
        await api.Owner.ExecuteAsync("""
            insert into station_operators (id, legal_name, name_ar, name_en, status, created_at, updated_at)
            values (@id, 'Op', 'مشغل', 'Op', 'ACTIVE', now(), now());
            insert into stations (id, operator_id, name_ar, name_en, time_zone, connectivity, fuel_types, status, created_at, updated_at)
            values (@station, @id, 'محطة', 'S', 'Africa/Cairo', 'NORMAL', '{GAS_92}', 'ACTIVE', now(), now());
            """, new { id, station = Guid.CreateVersion7() });
        return id;
    }
}
