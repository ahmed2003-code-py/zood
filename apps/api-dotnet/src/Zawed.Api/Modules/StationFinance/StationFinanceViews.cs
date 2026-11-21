using Zawed.Api.Modules.Identity.Contracts;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.StationFinance;

/// <summary>
/// Bank account changes and payouts as the signed-in caller reads them (Java <c>StationFinanceViews</c>): who asked and
/// who decided, by name for Operations and as ZAWED operations for a station (<see cref="IActorDirectory"/>, QA round 1).
/// </summary>
public sealed class StationFinanceViews(IActorDirectory people)
{
    public async Task<StationBankDetailsResponse> DetailsAsync(ZawedPrincipal viewer, BankDetails details, CancellationToken ct)
    {
        var accounts = new[] { details.Active, details.Pending }.OfType<BankAccount>();
        var actors = await people.DescribeAsync(viewer, accounts.SelectMany(a => new[] { (Guid?)a.RequestedBy, a.DecidedBy }), ct);
        return StationBankDetailsResponse.Of(details, actors);
    }

    public async Task<BankAccountResponse> BankAccountAsync(ZawedPrincipal viewer, BankAccount account, CancellationToken ct) =>
        (await BankAccountsAsync(viewer, [account], ct))[0];

    public async Task<Page<BankAccountResponse>> BankAccountsAsync(ZawedPrincipal viewer, Page<BankAccount> page, CancellationToken ct) =>
        new(await BankAccountsAsync(viewer, page.Items, ct), page.NextCursor);

    private async Task<IReadOnlyList<BankAccountResponse>> BankAccountsAsync(ZawedPrincipal viewer, IReadOnlyList<BankAccount> accounts,
        CancellationToken ct)
    {
        var actors = await people.DescribeAsync(viewer, accounts.SelectMany(a => new[] { (Guid?)a.RequestedBy, a.DecidedBy }), ct);
        return accounts.Select(a => BankAccountResponse.Of(a, actors)!).ToList();
    }

    public async Task<PayoutResponse> PayoutAsync(ZawedPrincipal viewer, Payout payout, CancellationToken ct) =>
        (await PayoutsAsync(viewer, [payout], ct))[0];

    public async Task<Page<PayoutResponse>> PayoutsAsync(ZawedPrincipal viewer, Page<Payout> page, CancellationToken ct) =>
        new(await PayoutsAsync(viewer, page.Items, ct), page.NextCursor);

    private async Task<IReadOnlyList<PayoutResponse>> PayoutsAsync(ZawedPrincipal viewer, IReadOnlyList<Payout> payouts, CancellationToken ct)
    {
        var actors = await people.DescribeAsync(viewer, payouts.SelectMany(p => new[] { (Guid?)p.RequestedBy, p.DecidedBy }), ct);
        return payouts.Select(p => PayoutResponse.Of(p, actors)).ToList();
    }
}
