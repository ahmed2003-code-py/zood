using System.Security.Cryptography;
using System.Text.Json;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Modules.Ledger;
using Zawed.Tests.Modules.Network;
using Zawed.Tests.Support;

namespace Zawed.Tests.Modules.Fueling;

/// <summary>A vehicle with an active tag (Java <c>PosTestSupport.Car</c>).</summary>
public sealed record Car(string CompanyId, string VehicleId, string TagUid);

/// <summary>
/// The fueling half of Java <c>PosTestSupport</c>: a working pump (the network half, <see cref="PosTestSupport"/>), and a
/// company with a vehicle, a tag, a price and money in the wallet, all set up through the real API the way Operations
/// would. Create one per test: it resets the in-memory rate limits.
/// </summary>
public sealed class FuelingTestSupport
{
    /// <summary>Piastres per litre.</summary>
    public const long Price = 1_500;

    private readonly LedgerHttp ledger;

    public FuelingTestSupport(ZawedApiFactory factory)
    {
        Pos = new PosTestSupport(factory);
        ledger = new LedgerHttp(Pos.Id);
    }

    public PosTestSupport Pos { get; }

    public IdentityTestSupport Id => Pos.Id;

    public Owner Owner => Pos.Owner;

    public Task<string> OpsAsync() => Pos.Ops.Value;

    public Task<Pump> PumpAsync(params string[] fuels) => Pos.PumpAsync(fuels);

    public async Task<string> CompanyAsync(long lowBalanceThresholdPiastres = 0) =>
        (await Id.SeedCompanyAsync(lowBalanceThresholdPiastres)).ToString();

    // ------------------------------------------------------------------------------------------------------- fleet

    public async Task<Car> CarAsync(string company, string fuel = "GAS_92", string tier = "NORMAL", long? vipCeilingMl = null,
        long tankMl = 60_000)
    {
        var plate = "FT " + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var ceiling = vipCeilingMl is null ? "" : $",\"vipMonthlyCeilingMl\":{vipCeilingMl}";
        var json = "{\"companyId\":\"" + company + "\",\"vehicle\":{\"plate\":\"" + plate + "\",\"make\":\"Toyota\",\"model\":\"Hilux\","
            + "\"fuelType\":\"" + fuel + "\",\"tankCapacityMl\":" + tankMl + ",\"tier\":\"" + tier + "\"" + ceiling + "}}";
        var vehicle = await Id.AuthedAsync("POST", "/api/v1/ops/vehicles", await OpsAsync(), json);
        var vehicleId = (await PosTestSupport.Json(vehicle, 201))["id"]!.GetValue<string>();
        var uid = RandomUid();
        await PosTestSupport.Json(await Id.AuthedAsync("POST", $"/api/v1/ops/vehicles/{vehicleId}/tags", await OpsAsync(),
            $$"""{"uid":"{{uid}}"}"""), 201);
        return new Car(company, vehicleId, uid);
    }

    public static string RandomUid() => Convert.ToHexString(RandomNumberGenerator.GetBytes(7));

    public async Task PriceAsync(string station, string fuel, long piastres) =>
        await PosTestSupport.Json(await Id.AuthedAsync("POST", "/api/v1/ops/prices", await OpsAsync(),
            $$"""{"fuelType":"{{fuel}}","stationId":"{{station}}","pricePiastres":{{piastres}}}"""), 201);

    /// <summary>Money in the wallet the way production puts it there: a recorded transfer that a second person approves.</summary>
    public async Task CreditAsync(string company, long piastres) =>
        await ledger.FundAsync(new SignedIn(await OpsAsync(), "", Guid.Empty), Guid.Parse(company), piastres);

    // ----------------------------------------------------------------------------------------------------- fueling

    public static string FuelingBody(Guid clientTxnId, string uid, long volumeMl, string? fuel) =>
        $$"""{"clientTxnId":"{{clientTxnId}}","tagUid":"{{uid}}","volumeMl":{{volumeMl}}{{(fuel is null ? "" : $",\"fuelType\":\"{fuel}\"")}},"deviceAt":"{{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}}","receiptLocale":"ar"}""";

    public Task<HttpResponseMessage> FuelAsync(Pump pump, Car car, long volumeMl) => FuelAsync(pump, car, Guid.NewGuid(), volumeMl, null);

    public Task<HttpResponseMessage> FuelAsync(Pump pump, Car car, Guid clientTxnId, long volumeMl, string? fuel) =>
        Pos.PosAsync("POST", "/api/v1/pos/fuelings", pump.DeviceToken, pump.Session, FuelingBody(clientTxnId, car.TagUid, volumeMl, fuel));

    public async Task<string> SignInAsync(Role role, Scope scope) => (await Id.SignInAsync(await Id.SeedAsync(role, scope, false))).AccessToken;

    public Task<HttpResponseMessage> GetAsync(string path, string token) => Id.AuthedAsync("GET", path, token);

    // ----------------------------------------------------------------------------------------------------- reading

    /// <summary>The status of a recorded fueling (a 201).</summary>
    public static async Task<string> StatusAsync(HttpResponseMessage response)
    {
        var body = await BodyAsync(response, 201);
        return body.GetProperty("status").GetString()!;
    }

    /// <summary>The reason of a declined fueling (a 201 with DECLINED).</summary>
    public static async Task<string> ReasonAsync(HttpResponseMessage response)
    {
        var body = await BodyAsync(response, 201);
        Assert.Equal("DECLINED", body.GetProperty("status").GetString());
        return body.GetProperty("declineReason").GetString()!;
    }

    public static async Task<JsonElement> BodyAsync(HttpResponseMessage response, int status)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True((int)response.StatusCode == status, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    public static async Task<string?> CodeAsync(HttpResponseMessage response) => await PosTestSupport.CodeAsync(response);

    public async Task<long> WalletBalanceAsync(string company) =>
        await Owner.QueryOneAsync<long>("select balance_piastres from company_wallets where company_id = @id::uuid", new { id = company });

    public async Task<long> CountAsync(string sql, object? args = null) => await Owner.QueryOneAsync<long>(sql, args);
}
