using Microsoft.AspNetCore.Mvc;
using Zawed.Api.Modules.Network.Contracts;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Sync;

/// <summary>
/// The snapshot a POS caches to work offline (B5 contract §2; Java <c>PosSnapshotController</c>). Device token only; no agent
/// needed. The POS chain authorises it by path (<c>PosChannel</c>); the snapshot is read in its own transaction.
/// </summary>
[Route("api/v1")]
public sealed class PosSnapshotController(Snapshots snapshots) : ApiControllerBase
{
    /// <summary>What this device needs to decide fuelings offline: tags (as digests), vehicles, limits, prices, wallet allowances.</summary>
    [HttpGet("pos/snapshot", Name = "getPosSnapshot"), Tags("pos"), OwnTransactions]
    [ProducesResponseType<PosSnapshot>(200)]
    public async Task<IActionResult> Snapshot(CancellationToken ct)
    {
        var snapshot = await snapshots.SnapshotForAsync(PosPrincipal.From(User)!, ct);
        Response.Headers.CacheControl = "no-store";
        return Ok(snapshot);
    }
}
