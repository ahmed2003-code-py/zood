using Zawed.Api.Modules.Identity.Contracts;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Ledger;

/// <summary>
/// Top-ups and wallet adjustments as the signed-in caller reads them (Java <c>LedgerViews</c>): who acted, by name where
/// the caller may see it (<see cref="IActorDirectory"/>), and the receiving account by its label (QA round 1).
/// </summary>
public sealed class LedgerViews(IActorDirectory people, FinanceSettings finance)
{
    public async Task<TopUpResponse> TopUpAsync(ZawedPrincipal viewer, TopUp topUp, CancellationToken ct) =>
        (await TopUpsAsync(viewer, [topUp], ct))[0];

    public async Task<Page<TopUpResponse>> TopUpsAsync(ZawedPrincipal viewer, Page<TopUp> page, CancellationToken ct) =>
        new(await TopUpsAsync(viewer, page.Items, ct), page.NextCursor);

    private async Task<IReadOnlyList<TopUpResponse>> TopUpsAsync(ZawedPrincipal viewer, IReadOnlyList<TopUp> topUps, CancellationToken ct)
    {
        var actors = await people.DescribeAsync(viewer, topUps.SelectMany(t => new[] { t.RequestedBy, t.VerifiedBy, t.DecidedBy }), ct);
        return topUps.Select(t => TopUpResponse.Of(t, actors, finance.LabelOf(t.ReceivingAccount))).ToList();
    }

    public async Task<WalletAdjustmentResponse> AdjustmentAsync(ZawedPrincipal viewer, Adjustment adjustment, CancellationToken ct) =>
        (await AdjustmentsAsync(viewer, [adjustment], ct))[0];

    public async Task<Page<WalletAdjustmentResponse>> AdjustmentsAsync(ZawedPrincipal viewer, Page<Adjustment> page, CancellationToken ct) =>
        new(await AdjustmentsAsync(viewer, page.Items, ct), page.NextCursor);

    private async Task<IReadOnlyList<WalletAdjustmentResponse>> AdjustmentsAsync(ZawedPrincipal viewer, IReadOnlyList<Adjustment> adjustments,
        CancellationToken ct)
    {
        var actors = await people.DescribeAsync(viewer, adjustments.SelectMany(a => new Guid?[] { a.RequestedBy, a.DecidedBy }), ct);
        return adjustments.Select(a => WalletAdjustmentResponse.Of(a, actors)).ToList();
    }
}
