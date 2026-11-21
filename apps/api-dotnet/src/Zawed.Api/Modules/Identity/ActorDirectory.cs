using Zawed.Api.Modules.Identity.Contracts;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;

namespace Zawed.Api.Modules.Identity;

/// <summary>The <see cref="IActorDirectory"/> of the identity module (Java <c>ActorDirectory</c>).</summary>
public sealed class ActorDirectory(IDbSession db) : IActorDirectory
{
    private sealed class Row
    {
        public Guid Id { get; set; }
        public string Full_Name { get; set; } = "";
        public string Role { get; set; } = "";
        public string Scope_Type { get; set; } = "";
        public Guid? Scope_Id { get; set; }
    }

    public async Task<Actors> DescribeAsync(ZawedPrincipal viewer, IEnumerable<Guid?> userIds, CancellationToken ct = default)
    {
        var wanted = userIds.Where(id => id is not null).Select(id => id!.Value).Distinct().ToArray();
        if (wanted.Length == 0)
        {
            return Actors.None;
        }
        var rows = (await db.Current.QueryAsync<Row>("select id, full_name, role, scope_type, scope_id from users where id = any(@ids)",
            new { ids = wanted }, ct)).ToDictionary(r => r.Id);
        var platform = viewer.Scope.Type == ScopeType.PLATFORM;
        var actors = new Dictionary<Guid, Actor>();
        foreach (var id in wanted)
        {
            rows.TryGetValue(id, out var row);
            if (platform)
            {
                actors[id] = row is null ? new Actor(null, null) : new Actor(row.Full_Name, Enum.Parse<ActorRole>(row.Role));
            }
            else if (row is not null && row.Scope_Type == viewer.Scope.Type.ToString() && row.Scope_Id == viewer.Scope.Id)
            {
                actors[id] = new Actor(row.Full_Name, Enum.Parse<ActorRole>(row.Role));
            }
            else
            {
                actors[id] = new Actor(null, ActorRole.ZAWED_OPERATIONS);
            }
        }
        return new Actors(actors);
    }
}
