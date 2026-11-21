using System.Text.Json;
using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Audit;

/// <summary>The audit trail's one writer (Java <c>AuditLog.record</c>), with the Java statement as it is.</summary>
internal sealed class AuditLog(TimeProvider clock, IHttpContextAccessor http) : IAuditLog
{
    public Task RecordAsync(DbTx tx, AuditActor actor, string action, string entityType, Guid? entityId, Scope scope,
        IReadOnlyDictionary<string, object?>? metadata, CancellationToken ct = default)
    {
        var request = http.HttpContext;
        var now = clock.GetUtcNow();
        return tx.ExecuteAsync("""
            insert into audit_log (id, occurred_at, actor_id, actor_role, action, entity_type, entity_id,
                                   scope_type, scope_id, metadata, request_id, ip_prefix)
            values (@id, @at, @actor, @role, @action, @entityType, @entityId, @scopeType, @scopeId,
                    cast(@metadata as jsonb), @requestId, @ip)
            """, new
        {
            id = Guid.CreateVersion7(now),
            at = now.UtcDateTime,
            actor = actor.Id,
            role = actor.Role?.ToString(),
            action,
            entityType,
            entityId,
            scopeType = scope.Type.ToString(),
            scopeId = scope.Id,
            metadata = JsonSerializer.Serialize(metadata ?? new Dictionary<string, object?>()),
            requestId = request is null ? null : RequestIds.Current(request),
            ip = request is null ? null : ClientInfo.PrefixOf(request.Connection.RemoteIpAddress),
        }, ct);
    }
}
