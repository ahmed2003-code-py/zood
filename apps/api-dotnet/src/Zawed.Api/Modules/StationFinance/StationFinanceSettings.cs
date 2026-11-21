using System.Globalization;

namespace Zawed.Api.Modules.StationFinance;

/// <summary>
/// Station finance's settings, read at boot under the Java names, as Java's <c>@Value</c> read them:
/// <list type="bullet">
/// <item><c>zawed.finance.bank-change-hold-hours</c> (default 72): <c>Zawed:Finance:BankChangeHoldHours</c> or Spring's
/// variable <c>ZAWED_FINANCE_BANKCHANGEHOLDHOURS</c>;</item>
/// <item><c>zawed.finance.payout-approval-threshold-piastres</c> (default 5,000,000, i.e. 50,000 EGP):
/// <c>Zawed:Finance:PayoutApprovalThresholdPiastres</c> or <c>ZAWED_FINANCE_PAYOUTAPPROVALTHRESHOLDPIASTRES</c>.</item>
/// </list>
/// A negative value stops the boot, as the Java constructors did.
/// </summary>
public sealed record StationFinanceSettings
{
    public const long DefaultHoldHours = 72;

    public const long DefaultApprovalThresholdPiastres = 5_000_000;

    public StationFinanceSettings(long bankChangeHoldHours, long payoutApprovalThresholdPiastres)
    {
        if (bankChangeHoldHours < 0)
        {
            throw new InvalidOperationException("zawed.finance.bank-change-hold-hours must not be negative: " + bankChangeHoldHours);
        }
        if (payoutApprovalThresholdPiastres < 0)
        {
            throw new InvalidOperationException(
                "zawed.finance.payout-approval-threshold-piastres must not be negative: " + payoutApprovalThresholdPiastres);
        }
        BankChangeHoldHours = bankChangeHoldHours;
        PayoutApprovalThresholdPiastres = payoutApprovalThresholdPiastres;
    }

    /// <summary>For how long no payout may go to a station after its bank account changed.</summary>
    public long BankChangeHoldHours { get; }

    /// <summary>A payout above this waits for a second person; at or below it, it posts at once.</summary>
    public long PayoutApprovalThresholdPiastres { get; }

    public TimeSpan Hold => TimeSpan.FromHours(BankChangeHoldHours);

    public static StationFinanceSettings From(IConfiguration configuration) => new(
        Long(configuration, "Zawed:Finance:BankChangeHoldHours", "ZAWED_FINANCE_BANKCHANGEHOLDHOURS", DefaultHoldHours),
        Long(configuration, "Zawed:Finance:PayoutApprovalThresholdPiastres", "ZAWED_FINANCE_PAYOUTAPPROVALTHRESHOLDPIASTRES",
            DefaultApprovalThresholdPiastres));

    private static long Long(IConfiguration configuration, string key, string variable, long fallback)
    {
        var value = configuration[key] ?? configuration[variable];
        return value is null ? fallback : long.Parse(value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }
}
