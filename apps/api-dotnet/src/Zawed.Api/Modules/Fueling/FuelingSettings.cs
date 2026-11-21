using System.Globalization;

namespace Zawed.Api.Modules.Fueling;

/// <summary>
/// The fueling engine's settings, read at boot under the Java names, each from <c>Zawed:...</c> or from the variable Spring
/// read for it, so Railway's variables do not change at the cutover:
/// <list type="bullet">
/// <item><c>zawed.fueling.vip-enabled</c> (default <c>false</c>): <c>Zawed:Fueling:VipEnabled</c>, <c>ZAWED_FUELING_VIPENABLED</c>;</item>
/// <item><c>zawed.sync.offline-exposure-cap-piastres</c> (default 1,000,000): <c>Zawed:Sync:OfflineExposureCapPiastres</c>,
/// <c>ZAWED_SYNC_OFFLINEEXPOSURECAPPIASTRES</c>;</item>
/// <item><c>zawed.sync.company-offline-alert-piastres</c> (default 2,000,000): <c>Zawed:Sync:CompanyOfflineAlertPiastres</c>,
/// <c>ZAWED_SYNC_COMPANYOFFLINEALERTPIASTRES</c>.</item>
/// </list>
/// </summary>
/// <param name="VipEnabled">
/// The VIP flow (PRD 4.4: not billed, no limits) is modelled but switched off until the owner decides who pays for VIP
/// fuel (plan §1, open question 4). While off, a VIP vehicle fuels exactly like a normal one: limits, balance, billed.
/// </param>
/// <param name="ExposureCapPiastres">
/// What one device may record offline for one company in 24 hours (B5 review 2). Past it an offline fueling is still
/// recorded, flagged <c>OVER_OFFLINE_ALLOWANCE</c>, its payable held and a critical alert raised.
/// </param>
/// <param name="CompanyOfflineAlertPiastres">
/// A company's offline fuelings across every device in 24 hours at which one <c>COMPANY_OFFLINE_EXPOSURE</c> alert is
/// raised (V14, ADR 0025). Monitoring only: it never changes a fueling.
/// </param>
public sealed record FuelingSettings(bool VipEnabled, long ExposureCapPiastres = FuelingSettings.DefaultExposureCap,
    long CompanyOfflineAlertPiastres = FuelingSettings.DefaultCompanyOfflineAlert)
{
    public const long DefaultExposureCap = 1_000_000;
    public const long DefaultCompanyOfflineAlert = 2_000_000;

    public static FuelingSettings From(IConfiguration configuration)
    {
        var value = configuration["Zawed:Fueling:VipEnabled"] ?? configuration["ZAWED_FUELING_VIPENABLED"];
        return new FuelingSettings(value is not null && bool.Parse(value.Trim()),
            Long(configuration, "Zawed:Sync:OfflineExposureCapPiastres", "ZAWED_SYNC_OFFLINEEXPOSURECAPPIASTRES", DefaultExposureCap),
            Long(configuration, "Zawed:Sync:CompanyOfflineAlertPiastres", "ZAWED_SYNC_COMPANYOFFLINEALERTPIASTRES", DefaultCompanyOfflineAlert));
    }

    /// <summary>A whole number of piastres; a value that does not read as one stops the boot, as Spring's conversion did.</summary>
    internal static long Long(IConfiguration configuration, string key, string variable, long fallback)
    {
        var value = configuration[key] ?? configuration[variable];
        return value is null ? fallback : long.Parse(value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }
}
