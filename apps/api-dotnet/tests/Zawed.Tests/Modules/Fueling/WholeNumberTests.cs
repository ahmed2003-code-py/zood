using Zawed.Api.Platform.Security;
using Zawed.Tests.Modules.Ledger;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Fueling.FuelingTestSupport;

namespace Zawed.Tests.Modules.Fueling;

/// <summary>
/// Money is integer piastres and volume integer millilitres (QA round 1; Java <c>WholeNumberTests</c>). A fractional JSON
/// number in such a field is refused with 400 VALIDATION_FAILED naming the field; it is never truncated to the integer below,
/// on any endpoint, the POS included. The offline half (<c>aFractionalVolumeInAnOfflineBatchRejectsThatItemOnly</c>) comes
/// with the sync endpoint in wave 4A.
/// </summary>
public sealed class WholeNumberTests(SharedApi api) : IClassFixture<SharedApi>
{
    private static readonly string Today = LedgerHttp.Today.ToString("yyyy-MM-dd");

    private readonly FuelingTestSupport f = new(api.Factory);

    [Fact]
    public async Task ACompanyTopUpOfAFractionalAmountIsRefusedNotTruncated()
    {
        var company = await f.Id.SeedCompanyAsync();
        var fleet = await f.SignInAsync(Role.FLEET_MANAGER, Scope.Company(company));
        var reference = "FT-" + Guid.NewGuid();

        await AssertRefusedAsync(await f.Id.AuthedAsync("POST", "/api/v1/company/topups", fleet,
            $$"""{"amountPiastres":100.5,"bankReference":"{{reference}}","transferDate":"{{Today}}"}"""), "amountPiastres");
        Assert.Equal(0, await f.CountAsync("select count(*) from topup_requests where company_id = @company", new { company }));

        // A whole number still works, and so does the same value written as a JSON integer.
        var accepted = await BodyAsync(await f.Id.AuthedAsync("POST", "/api/v1/company/topups", fleet,
            $$"""{"amountPiastres":100,"bankReference":"{{reference}}","transferDate":"{{Today}}"}"""), 201);
        Assert.Equal(100, accepted.GetProperty("amountPiastres").GetInt64());
    }

    [Fact]
    public async Task AFloatThatHappensToBeWholeIsRefusedToo()
    {
        var company = await f.Id.SeedCompanyAsync();
        var fleet = await f.SignInAsync(Role.FLEET_MANAGER, Scope.Company(company));
        foreach (var amount in new[] { "100.0", "1e2", "100.000001" })
        {
            await AssertRefusedAsync(await f.Id.AuthedAsync("POST", "/api/v1/company/topups", fleet,
                $$"""{"amountPiastres":{{amount}},"bankReference":"FT-{{Guid.NewGuid()}}","transferDate":"{{Today}}"}"""), "amountPiastres");
        }
    }

    [Fact]
    public async Task OpsMoneyFieldsRefuseFractions()
    {
        var company = await f.Id.SeedCompanyAsync();
        var ops = await f.OpsAsync();

        await AssertRefusedAsync(await f.Id.AuthedAsync("POST", $"/api/v1/ops/companies/{company}/wallet/adjustments", ops,
            """{"amountPiastres":2500.25,"reason":"goodwill"}"""), "amountPiastres");
        await AssertRefusedAsync(await f.Id.AuthedAsync("POST", $"/api/v1/ops/companies/{company}/wallet/credits", ops,
            $$"""{"amountPiastres":2500.75,"bankReference":"FT-{{Guid.NewGuid()}}","transferDate":"{{Today}}"}"""), "amountPiastres");
        Assert.Equal(0, await f.WalletBalanceAsync(company.ToString()));
        Assert.Equal(0, await f.CountAsync("select count(*) from wallet_adjustments where company_id = @company", new { company }));

        // A nested field is named by its path.
        await AssertRefusedAsync(await f.Id.AuthedAsync("POST", "/api/v1/ops/companies", ops,
            """{"company":{"legalName":"F","nameAr":"ف","nameEn":"F"},"lowBalanceThresholdPiastres":10.5}"""), "lowBalanceThresholdPiastres");
    }

    [Fact]
    public async Task LimitsAndVolumesRefuseFractions()
    {
        var car = await f.CarAsync((await f.Id.SeedCompanyAsync()).ToString());
        var ops = await f.OpsAsync();

        await AssertRefusedAsync(await f.Id.AuthedAsync("PUT", $"/api/v1/ops/vehicles/{car.VehicleId}/limits", ops,
            """{"kind":"AMOUNT","period":"DAY","maxValue":500050.5}"""), "maxValue");
        await AssertRefusedAsync(await f.Id.AuthedAsync("PUT", $"/api/v1/ops/vehicles/{car.VehicleId}/limits", ops,
            """{"kind":"VOLUME","period":"DAY","maxValue":40000.5}"""), "maxValue");
        Assert.Equal(0, await f.CountAsync("select count(*) from limit_rules where vehicle_id = @v::uuid", new { v = car.VehicleId }));

        await AssertRefusedAsync(await f.Id.AuthedAsync("POST", "/api/v1/ops/vehicles", ops,
            "{\"companyId\":\"" + car.CompanyId + "\",\"vehicle\":{\"plate\":\"WN " + Guid.NewGuid().ToString()[..6]
                + "\",\"make\":\"M\",\"model\":\"M\",\"fuelType\":\"GAS_92\",\"tankCapacityMl\":50000.5,\"tier\":\"NORMAL\"}}"),
            "vehicle.tankCapacityMl");
    }

    [Fact]
    public async Task ThePosRefusesAFractionalVolume()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await f.CompanyAsync();
        await f.CreditAsync(company, 1_000_000);
        var car = await f.CarAsync(company);
        var clientTxnId = Guid.NewGuid();

        await AssertRefusedAsync(await f.Pos.PosAsync("POST", "/api/v1/pos/fuelings", pump.DeviceToken, pump.Session,
            $$"""{"clientTxnId":"{{clientTxnId}}","tagUid":"{{car.TagUid}}","volumeMl":20000.5,"deviceAt":"{{DateTimeOffset.UtcNow:O}}","receiptLocale":"ar"}"""),
            "volumeMl");
        Assert.Equal(0, await f.CountAsync("select count(*) from fuelings where client_txn_id = @clientTxnId", new { clientTxnId }));
        Assert.Equal(1_000_000, await f.WalletBalanceAsync(company));

        // A whole number written as a float is still refused online: the terminal is told at once.
        await AssertRefusedAsync(await f.Pos.PosAsync("POST", "/api/v1/pos/fuelings", pump.DeviceToken, pump.Session,
            $$"""{"clientTxnId":"{{Guid.NewGuid()}}","tagUid":"{{car.TagUid}}","volumeMl":20000.0,"deviceAt":"{{DateTimeOffset.UtcNow:O}}","receiptLocale":"ar"}"""),
            "volumeMl");
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string field)
    {
        var text = await response.Content.ReadAsStringAsync();
        var body = await BodyAsync(response, 400);
        Assert.Equal("VALIDATION_FAILED", body.GetProperty("code").GetString());
        Assert.Equal(field, body.GetProperty("fields")[0].GetProperty("field").GetString());
        Assert.Equal("INTEGER_REQUIRED", body.GetProperty("fields")[0].GetProperty("code").GetString());
        Assert.DoesNotContain("100.5", text, StringComparison.Ordinal); // the value is not echoed
        Assert.DoesNotContain("20000.5", text, StringComparison.Ordinal);
    }
}
