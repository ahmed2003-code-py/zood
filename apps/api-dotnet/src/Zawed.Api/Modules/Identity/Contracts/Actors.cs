using Zawed.Api.Platform.Security;

namespace Zawed.Api.Modules.Identity.Contracts;

/// <summary>The role shown beside a name. A tenant sees ZAWED's staff only as <see cref="ZAWED_OPERATIONS"/>.</summary>
public enum ActorRole
{
    SUPER_ADMIN,
    OPS_MANAGER,
    OPS,
    FLEET_MANAGER,
    ACCOUNTANT,
    STATION_MANAGER,

    /// <summary>A platform user, as a company or a station reads them: the team, not the person.</summary>
    ZAWED_OPERATIONS,
}

/// <summary>One person as the caller may see them: the name is null when the caller may not read it.</summary>
public sealed record Actor(string? Name, ActorRole? Role);

/// <summary>
/// How the people who acted on a record are shown to the signed-in caller (QA round 1; Java <c>Actors</c>), from
/// <see cref="IActorDirectory.DescribeAsync"/>. A response record asks it for a name and a role per user id; an id that
/// is null (nobody acted yet) has neither.
/// </summary>
public sealed class Actors(IReadOnlyDictionary<Guid, Actor> byId)
{
    public static readonly Actors None = new(new Dictionary<Guid, Actor>());

    private readonly Dictionary<Guid, Actor> byId = new(byId);

    /// <summary>The display name, or null: nobody acted, the caller may not read it, or the user is unknown.</summary>
    public string? Name(Guid? userId) => userId is { } id && byId.TryGetValue(id, out var actor) ? actor.Name : null;

    /// <summary>
    /// The user id as the caller may see it: null for a ZAWED staff member read by a company or a station, who sees
    /// only <see cref="ActorRole.ZAWED_OPERATIONS"/>; the id otherwise.
    /// </summary>
    public Guid? Id(Guid? userId) =>
        userId is { } id && byId.TryGetValue(id, out var actor) && actor.Role == ActorRole.ZAWED_OPERATIONS ? null : userId;

    /// <summary>The role label, or null when nobody acted.</summary>
    public ActorRole? Role(Guid? userId) => userId is { } id && byId.TryGetValue(id, out var actor) ? actor.Role : null;
}

/// <summary>
/// Names for the user ids that maker-checker records carry (top-ups, wallet adjustments, bank account changes,
/// payouts), so a card reads "Verified by Hana" instead of an id (QA round 1; Java <c>ActorDirectory</c>).
/// <list type="bullet">
/// <item>A platform caller (Super Admin, Ops Manager, Ops) sees every name and role.</item>
/// <item>A company or station caller sees the names and roles of the users of its own scope. Any other actor is a
/// platform user and is shown as <see cref="ActorRole.ZAWED_OPERATIONS"/> with no name, so no staff name or e-mail
/// reaches a tenant.</item>
/// </list>
/// The read runs in the caller's (request) transaction, so row-level security on <c>users</c> already hides every user
/// outside it; the code checks the scope of each row again and never returns an e-mail.
/// </summary>
public interface IActorDirectory
{
    /// <summary>How each of these users is shown to <paramref name="viewer"/>. Nulls are ignored.</summary>
    Task<Actors> DescribeAsync(ZawedPrincipal viewer, IEnumerable<Guid?> userIds, CancellationToken ct = default);
}
