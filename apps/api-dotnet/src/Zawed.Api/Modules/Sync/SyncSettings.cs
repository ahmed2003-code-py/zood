using System.Globalization;

namespace Zawed.Api.Modules.Sync;

/// <summary>
/// The snapshot's settings, read at boot under the Java names (each from <c>Zawed:...</c> or from the variable Spring read
/// for it, so Railway's variables do not change at the cutover):
/// <list type="bullet">
/// <item><c>zawed.sync.offline-allowance-cap-piastres</c> (default 500,000, 5,000 EGP): <c>Zawed:Sync:OfflineAllowanceCapPiastres</c>,
/// <c>ZAWED_SYNC_OFFLINEALLOWANCECAPPIASTRES</c>;</item>
/// <item><c>zawed.fueling.vip-enabled</c> (default <c>false</c>), which the snapshot reports and applies to the vehicles:
/// <c>Zawed:Fueling:VipEnabled</c>, <c>ZAWED_FUELING_VIPENABLED</c>. Java read it in both places with <c>@Value</c> too.</item>
/// </list>
/// </summary>
public sealed record SyncSettings(long OfflineAllowanceCapPiastres, bool VipEnabled)
{
    public const long DefaultOfflineAllowanceCap = 500_000;

    public static SyncSettings From(IConfiguration configuration)
    {
        var cap = configuration["Zawed:Sync:OfflineAllowanceCapPiastres"] ?? configuration["ZAWED_SYNC_OFFLINEALLOWANCECAPPIASTRES"];
        var vip = configuration["Zawed:Fueling:VipEnabled"] ?? configuration["ZAWED_FUELING_VIPENABLED"];
        return new SyncSettings(
            cap is null ? DefaultOfflineAllowanceCap : long.Parse(cap.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
            vip is not null && bool.Parse(vip.Trim()));
    }
}
