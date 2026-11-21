using Zawed.Api.Platform.Security;

namespace Zawed.Api.Platform.Persistence;

/// <summary>
/// What a database transaction is told about whose data it works on: the scope, and the acting user if
/// there is one (Java <c>DbScope.Context</c>). Row-level security reads it, so it decides which rows a
/// query can see, whatever the query says.
/// <para>Normally it is the signed-in user's (<see cref="ForPrincipal"/>). <see cref="Escalate"/> builds any
/// other scope and is allowed only in reviewed places, each after its own proof is checked (the sign-in
/// path, the POS channel, the fueling engine, offline sync, the fleet import worker, jobs).
/// <c>ArchitectureTests.ScopeEscalationsAreTheReviewedOnesOnly</c> keeps that list closed, as the Java
/// <c>ArchitectureTests.ESCALATIONS</c> did for <c>DbScope.runAs</c>.</para>
/// </summary>
public sealed record DbScopeContext
{
    private DbScopeContext(Scope? scope, Guid? userId)
    {
        Scope = scope;
        UserId = userId;
    }

    /// <summary>Null: nobody is identified, and a scoped table shows no rows (fails safe).</summary>
    public Scope? Scope { get; }

    public Guid? UserId { get; }

    /// <summary>No scope at all: what an anonymous request's transaction is told.</summary>
    public static readonly DbScopeContext None = new(null, null);

    public static DbScopeContext ForPrincipal(ZawedPrincipal? principal) =>
        principal is null ? None : new(principal.Scope, principal.UserId);

    /// <summary>
    /// An explicit scope that does not come from the request's principal. Every caller is listed, with its
    /// reason, in <c>ArchitectureTests</c>; a new caller fails that test until it is reviewed.
    /// </summary>
    public static DbScopeContext Escalate(Scope scope, Guid? actingUserId) => new(scope, actingUserId);
}
