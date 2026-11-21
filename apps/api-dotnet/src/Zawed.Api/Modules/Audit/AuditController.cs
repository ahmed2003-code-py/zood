using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Audit;

/// <summary>One row of the audit trail.</summary>
public sealed record AuditEntry(Guid Id, DateTimeOffset OccurredAt, Guid? ActorId, string? ActorRole, string Action, string EntityType,
    Guid? EntityId, string ScopeType, Guid? ScopeId, IReadOnlyDictionary<string, JsonElement> Metadata, string? RequestId, string? IpPrefix);

/// <summary>Read access to the audit trail (Java <c>AuditController</c>). Row-level security limits a scoped reader to its own scope.</summary>
[Route(ApiContract.BasePath + "/admin/audit")]
[Tags("audit")]
public sealed class AuditController(IDbSession db) : ApiControllerBase
{
    private sealed class Row
    {
        public Guid Id { get; set; }
        public DateTime Occurred_At { get; set; }
        public Guid? Actor_Id { get; set; }
        public string? Actor_Role { get; set; }
        public string Action { get; set; } = "";
        public string Entity_Type { get; set; } = "";
        public Guid? Entity_Id { get; set; }
        public string Scope_Type { get; set; } = "";
        public Guid? Scope_Id { get; set; }
        public string Metadata { get; set; } = "{}";
        public string? Request_Id { get; set; }
        public string? Ip_Prefix { get; set; }
    }

    /// <summary>Audit entries, newest first</summary>
    [HttpGet(Name = "listAudit")]
    [RequiresPermission(Permission.AUDIT_READ)]
    [EndpointSummary("Audit entries, newest first")]
    [ProducesResponseType<Page<AuditEntry>>(200)]
    public async Task<IActionResult> List([FromQuery] Guid? actorId, [FromQuery] string? entityType, [FromQuery] Guid? entityId,
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] string? cursor, [FromQuery] int? limit,
        CancellationToken ct)
    {
        var size = Page<AuditEntry>.Limit(limit);
        var rows = await db.Current.QueryAsync<Row>("""
            select id, occurred_at, actor_id, actor_role, action, entity_type, entity_id, scope_type, scope_id,
                   metadata::text as metadata, request_id, ip_prefix
            from audit_log
            where (cast(@actor as uuid) is null or actor_id = @actor)
              and (cast(@entityType as text) is null or entity_type = @entityType)
              and (cast(@entityId as uuid) is null or entity_id = @entityId)
              and (cast(@from as timestamptz) is null or occurred_at >= @from)
              and (cast(@to as timestamptz) is null or occurred_at < @to)
              and (cast(@cursor as uuid) is null or id < @cursor)
            order by id desc
            limit @limit
            """, new
        {
            actor = actorId,
            entityType,
            entityId,
            from = from?.UtcDateTime,
            to = to?.UtcDateTime,
            cursor = Cursors.Parse(cursor),
            limit = size + 1,
        }, ct);
        var entries = rows.Select(r => new AuditEntry(r.Id, new DateTimeOffset(DateTime.SpecifyKind(r.Occurred_At, DateTimeKind.Utc)),
            r.Actor_Id, r.Actor_Role, r.Action, r.Entity_Type, r.Entity_Id, r.Scope_Type, r.Scope_Id,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(r.Metadata)!, r.Request_Id, r.Ip_Prefix)).ToList();
        return Ok(Page<AuditEntry>.Of(entries, size, e => e.Id));
    }
}
