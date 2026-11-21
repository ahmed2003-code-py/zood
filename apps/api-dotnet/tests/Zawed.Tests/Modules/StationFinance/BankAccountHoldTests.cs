using Zawed.Tests.Support;
using static Zawed.Tests.Modules.StationFinance.FinanceTestSupport;

namespace Zawed.Tests.Modules.StationFinance;

/// <summary>
/// A newly approved bank account receives nothing during the hold window (default 72 hours, roadmap delta A3; Java
/// <c>BankAccountHoldTests</c>): a fraudulent change has time to be noticed before money follows it. Runs with the
/// default settings.
/// </summary>
public sealed class BankAccountHoldTests(SharedApi api) : IClassFixture<SharedApi>
{
    private readonly FinanceTestSupport f = new(api.Factory);

    [Fact]
    public async Task NoPayoutGoesToANewAccountDuringTheHold()
    {
        var (pump, _, car) = await f.StationWithCustomerAsync(credit: 100_000);
        await f.FuelAsync(pump, car, 20_000);
        var station = pump.StationId;
        var ops = await f.OpsAsync();

        var change = await f.RequestBankChangeAsync(ops, station, "EG380019000500000000263180002");
        var approved = await BodyAsync(await f.AuthedAsync("POST", $"/api/v1/ops/bank-account-changes/{change}/approve", await f.OpsAsync()), 200);
        var heldUntil = DateTimeOffset.Parse(approved.GetProperty("payoutsHeldUntil").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(heldUntil > DateTimeOffset.UtcNow.AddHours(71), heldUntil.ToString("O"));

        var payout = await f.PayoutAsync(ops, station, 1_000, "PO-" + Guid.NewGuid());
        var error = await Http.AssertErrorAsync(payout, 409, "PAYOUT_ON_HOLD");
        // The params carry the instant as Java's Instant.toString() wrote it, which the stored value reads back as.
        Assert.Equal(heldUntil, DateTimeOffset.Parse(error.GetProperty("params").GetProperty("heldUntil").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture));
    }
}
