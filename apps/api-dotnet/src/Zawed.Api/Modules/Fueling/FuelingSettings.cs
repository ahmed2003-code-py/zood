namespace Zawed.Api.Modules.Fueling;

/// <summary>
/// The fueling engine's settings, read at boot under the Java names (<c>zawed.fueling.vip-enabled</c>, default
/// <c>false</c>): <c>Zawed:Fueling:VipEnabled</c> or Spring's variable <c>ZAWED_FUELING_VIPENABLED</c>.
/// </summary>
/// <param name="VipEnabled">
/// The VIP flow (PRD 4.4: not billed, no limits) is modelled but switched off until the owner decides who pays for VIP
/// fuel (plan §1, open question 4). While off, a VIP vehicle fuels exactly like a normal one: limits, balance, billed.
/// </param>
public sealed record FuelingSettings(bool VipEnabled)
{
    public static FuelingSettings From(IConfiguration configuration)
    {
        var value = configuration["Zawed:Fueling:VipEnabled"] ?? configuration["ZAWED_FUELING_VIPENABLED"];
        return new FuelingSettings(value is not null && bool.Parse(value.Trim()));
    }
}
