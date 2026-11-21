using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Zawed.Api.Platform.Security;

namespace Zawed.Api.Platform.Persistence;

/// <summary>An entity owned by one company. EF filters it to the transaction's scope (second layer to RLS).</summary>
public interface ICompanyOwned
{
    Guid CompanyId { get; }
}

/// <summary>An entity owned by one station operator. Same filter, by operator.</summary>
public interface IOperatorOwned
{
    Guid OperatorId { get; }
}

/// <summary>
/// Optimistic concurrency on a row that two people may edit (the playbook's <c>rowversion</c>): PostgreSQL's
/// <c>xmin</c> system column, mapped as the concurrency token. A stale update raises
/// <see cref="DbUpdateConcurrencyException"/>, which answers <c>409 CONFLICT</c>. Not for money: balances
/// change only under <c>FOR UPDATE</c> (invariant 5).
/// </summary>
public interface IVersioned
{
    uint Version { get; set; }
}

/// <summary>
/// The one EF Core context. The database is the playbook's Unit of Work and each <c>DbSet</c> its
/// repository (Shifa style); the context always runs on a <see cref="DbTx"/>'s connection and
/// transaction, so it never sees rows outside the transaction's scope.
/// <para>Two layers keep tenants apart: PostgreSQL row-level security, which is the authority (V3), and
/// the global query filters here, which make a mistake visible in tests before RLS has to catch it.
/// <see cref="SaveChangesAsync(CancellationToken)"/> also refuses to write a company- or operator-owned row
/// outside the scope.</para>
/// <para>Modules add their entities with an <see cref="IEntityTypeConfiguration{TEntity}"/> in their own
/// folder; the context picks them up from the assembly, so no module edits this file. The schema is never
/// created or migrated by EF: the SQL migrations own it (ADR 0004, ADR 0029).</para>
/// </summary>
public sealed class ZawedDbContext : DbContext
{
    private readonly DbScopeContext scope;

    public ZawedDbContext(DbContextOptions<ZawedDbContext> options, DbScopeContext scope) : base(options) => this.scope = scope;

    // Read by the query filters at query time (EF parameterises members of the context).
    internal bool ScopeIsPlatform => scope.Scope?.IsPlatform == true;
    internal Guid ScopeCompanyId => scope.Scope is { Type: ScopeType.COMPANY, Id: { } id } ? id : Guid.Empty;
    internal Guid ScopeOperatorId => scope.Scope is { Type: ScopeType.OPERATOR, Id: { } id } ? id : Guid.Empty;

    internal static ZawedDbContext Create(DbTx tx)
    {
        var options = new DbContextOptionsBuilder<ZawedDbContext>()
            .UseNpgsql(tx.Connection)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;
        var context = new ZawedDbContext(options, tx.Scope);
        context.Database.UseTransaction(tx.Transaction);
        context.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;
        return context;
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configuration)
    {
        // Enums are stored as their names, which are the database's CHECK values (ADR 0029).
        configuration.Properties<Enum>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.ApplyConfigurationsFromAssembly(typeof(ZawedDbContext).Assembly);
        foreach (var type in model.Model.GetEntityTypes().Select(t => t.ClrType).ToList())
        {
            if (typeof(ICompanyOwned).IsAssignableFrom(type))
            {
                Apply(model, type, nameof(CompanyFilter));
            }
            else if (typeof(IOperatorOwned).IsAssignableFrom(type))
            {
                Apply(model, type, nameof(OperatorFilter));
            }
            if (typeof(IVersioned).IsAssignableFrom(type))
            {
                model.Entity(type).Property(nameof(IVersioned.Version)).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            }
        }
    }

    private void Apply(ModelBuilder model, Type type, string filter) =>
        GetType().GetMethod(filter, BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(type).Invoke(this, [model]);

    private void CompanyFilter<T>(ModelBuilder model) where T : class, ICompanyOwned
    {
        Expression<Func<T, bool>> filter = e => ScopeIsPlatform || e.CompanyId == ScopeCompanyId;
        model.Entity<T>().HasQueryFilter(filter);
    }

    private void OperatorFilter<T>(ModelBuilder model) where T : class, IOperatorOwned
    {
        Expression<Func<T, bool>> filter = e => ScopeIsPlatform || e.OperatorId == ScopeOperatorId;
        model.Entity<T>().HasQueryFilter(filter);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (!ScopeIsPlatform)
        {
            var leak = ChangeTracker.Entries()
                .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .FirstOrDefault(e => e.Entity is ICompanyOwned c && c.CompanyId != ScopeCompanyId
                    || e.Entity is IOperatorOwned o && o.OperatorId != ScopeOperatorId);
            if (leak is not null)
            {
                throw new CrossScopeWriteException(leak.Metadata.ClrType.Name);
            }
        }
        return base.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// A write outside the transaction's scope that RLS would also refuse. Answers 404, like any row outside
/// the scope (invariant 8), and is logged as a warning: it is always a bug.
/// </summary>
public sealed class CrossScopeWriteException(string entity) : Exception($"Cross-scope write of {entity} blocked")
{
    public string Entity { get; } = entity;
}
