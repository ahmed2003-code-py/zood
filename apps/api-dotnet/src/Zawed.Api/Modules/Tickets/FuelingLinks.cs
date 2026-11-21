using Zawed.Api.Platform.Persistence;

namespace Zawed.Api.Modules.Tickets;

/// <summary>
/// STAND-IN (wave 3A): whether the caller may see a fueling, read straight from the <c>fuelings</c> table with the Java
/// statement of <c>Tickets.requireVisible("fuelings", ...)</c>, in the caller's scope so row-level security decides. Replace
/// it with the fueling module's contract once 3A publishes one (<c>IFuelingApi</c>), and delete this file.
/// </summary>
internal static class FuelingLinks
{
    public static Task<bool> VisibleAsync(DbTx tx, Guid id, CancellationToken ct) =>
        tx.QuerySingleAsync<bool>("select exists (select 1 from fuelings where id = @id)", new { id }, ct);
}
