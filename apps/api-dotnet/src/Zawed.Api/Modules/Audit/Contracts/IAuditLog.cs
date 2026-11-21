using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;

namespace Zawed.Api.Modules.Audit.Contracts;

/// <summary>Who acted. A null id is the system or an anonymous caller (a failed sign-in).</summary>
public sealed record AuditActor(Guid? Id, Role? Role)
{
    public static readonly AuditActor Anonymous = new(null, null);
}

/// <summary>
/// The append-only audit trail (PRD §6.4, Java <c>AuditLog</c>). Every privileged change records who did what to
/// which entity, in the same transaction as the change: if the change rolls back, so does its record, and a
/// change cannot commit without one. The runtime role may insert rows and never update or delete them (V3).
/// </summary>
public interface IAuditLog
{
    /// <summary>
    /// Records an action in <paramref name="tx"/>, the transaction that makes the change (Java's MANDATORY
    /// propagation). The request id and the address prefix come from the current request, when there is one.
    /// </summary>
    /// <param name="scope">The scope of the entity acted on, which decides who may later read the row.</param>
    /// <param name="metadata">Facts that explain the action (a reason, the new status); never secrets.</param>
    Task RecordAsync(DbTx tx, AuditActor actor, string action, string entityType, Guid? entityId, Scope scope,
        IReadOnlyDictionary<string, object?>? metadata, CancellationToken ct = default);
}
