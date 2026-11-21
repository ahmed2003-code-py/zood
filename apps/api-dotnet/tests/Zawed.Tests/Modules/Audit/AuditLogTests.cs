using Microsoft.Extensions.DependencyInjection;
using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Support;

namespace Zawed.Tests.Modules.Audit;

/// <summary>
/// The audit trail is written in the transaction of the change it records (PRD §6.4, Java <c>AuditLog</c> with MANDATORY
/// propagation): a change that rolls back takes its record with it, and a record that commits says who, what and
/// where, with its metadata as given.
/// </summary>
public sealed class AuditLogTests(SharedApi api) : IClassFixture<SharedApi>
{
    private ITransactions Transactions => api.Factory.Services.GetRequiredService<ITransactions>();

    private IAuditLog Audit => api.Factory.Services.GetRequiredService<IAuditLog>();

    [Fact]
    public async Task ARecordCommitsWithItsChangeAndCarriesActorScopeAndMetadata()
    {
        var entity = Guid.CreateVersion7();
        var actor = Guid.CreateVersion7();
        var company = Guid.CreateVersion7();
        await Transactions.RunAsync(DbScopeContext.Escalate(Scope.Company(company), actor), async tx =>
        {
            await Audit.RecordAsync(tx, new AuditActor(actor, Role.FLEET_MANAGER), "AUDIT_PROBE", "USER", entity, Scope.Company(company),
                new Dictionary<string, object?> { ["reason"] = "probe", ["count"] = 3, ["flag"] = true });
            return true;
        });

        var row = await api.Factory.Owner.QueryOneAsync<(Guid, string, string, string, Guid, string, int, bool, string?)>("""
            select actor_id, actor_role, action, scope_type, scope_id, metadata ->> 'reason', (metadata ->> 'count')::int,
                   (metadata ->> 'flag')::boolean, request_id
            from audit_log where entity_id = @entity
            """, new { entity });
        Assert.Equal((actor, "FLEET_MANAGER", "AUDIT_PROBE", "COMPANY", company, "probe", 3, true, (string?)null), row);
    }

    [Fact]
    public async Task ARecordRollsBackWithItsChange()
    {
        var entity = Guid.CreateVersion7();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Transactions.RunAsync<bool>(DbScopeContext.Escalate(Scope.Platform, null), async tx =>
        {
            await Audit.RecordAsync(tx, AuditActor.Anonymous, "AUDIT_PROBE", "USER", entity, Scope.Platform, null);
            throw new InvalidOperationException("the change fails after its record was written");
        }));
        Assert.Equal(0L, await api.Factory.Owner.QueryOneAsync<long>("select count(*) from audit_log where entity_id = @entity", new { entity }));
    }

    [Fact]
    public async Task AScopedCallerCannotWriteARecordAboutAnotherScope()
    {
        // audit_log_append (V3): the row's scope must be the transaction's, unless the caller is the platform.
        var entity = Guid.CreateVersion7();
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Transactions.RunAsync(
            DbScopeContext.Escalate(Scope.Company(Guid.CreateVersion7()), null), async tx =>
            {
                await Audit.RecordAsync(tx, AuditActor.Anonymous, "AUDIT_PROBE", "USER", entity, Scope.Company(Guid.CreateVersion7()), null);
                return true;
            }));
        Assert.Contains("row-level security", ex.Message, StringComparison.Ordinal);
    }
}
