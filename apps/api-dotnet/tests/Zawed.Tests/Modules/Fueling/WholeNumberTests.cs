using Zawed.Api.Platform.Security;
using Zawed.Tests.Modules.Ledger;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Fueling.FuelingTestSupport;

namespace Zawed.Tests.Modules.Fueling;

/// <summary>
/// Money is integer piastres and volume integer millilitres (QA round 1; Java <c>WholeNumberTests</c>). A fractional JSON
/// number in such a field is refused with 400 VALIDATION_FAILED naming the field; it is never truncated to the integer below,
/// on any endpoint, the POS included, except inside an offline batch, where one item's fraction rejects that item only.
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

    /// <summary>
    /// Offline, one bad item never holds back the rest (B5 contract §3, invariant 7). Refusing the whole batch for one
    /// fractional volume would leave the device resending the same 100 items forever. A whole number written as a float
    /// (<c>20000.0</c>) is that number; a real fraction is that item's REJECTED INVALID_VOLUME, with its SYNC_REJECTED alert,
    /// and every other item is recorded.
    /// </summary>
    [Fact]
    public async Task AFractionalVolumeInAnOfflineBatchRejectsThatItemOnly()
    {
        var o = new OfflineTestSupport(api.Factory);
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = (await f.Id.SeedCompanyAsync()).ToString();
        await f.CreditAsync(company, 10_000_000);
        var car = await f.CarAsync(company);
        var batchId = Guid.NewGuid();
        var items = new List<string>();
        for (var i = 0; i < 100; i++)
        {
            var json = (await o.CaptureAsync(pump, car, 1_000)).Json();
            if (i == 10)
            {
                json = json.Replace("\"volumeMl\":1000", "\"volumeMl\":1500.25", StringComparison.Ordinal);
            }
            if (i == 50)
            {
                json = json.Replace("\"volumeMl\":1000", "\"volumeMl\":20000.0", StringComparison.Ordinal);
            }
            items.Add(json);
        }
        var answered = await OfflineTestSupport.AnsweredAsync(await f.Pos.PosAsync("POST", "/api/v1/pos/sync", pump.DeviceToken, null,
            "{\"batchId\":\"" + batchId + "\",\"pending\":0,\"items\":[" + string.Join(",", items) + "]}"));

        var results = answered.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("result").GetString()).ToList();
        Assert.Equal(100, results.Count);
        Assert.Equal("INVALID_VOLUME", OfflineTestSupport.Rejection(answered, 10));
        Assert.Equal(1, results.Count(r => r == "REJECTED"));
        Assert.Equal(99, results.Count(r => r == "RECORDED"));
        Assert.Equal(20_000, OfflineTestSupport.Item(answered, 50).GetProperty("fueling").GetProperty("volumeMl").GetInt64());
        Assert.Equal(99, await f.CountAsync("select count(*) from fuelings where company_id = @c::uuid", new { c = company }));
        Assert.Equal(1, await f.CountAsync("select count(*) from sync_batches where device_batch_id = @batchId", new { batchId }));
        var details = await f.Owner.QueryOneAsync<string>(
            "select details::text from alerts where type = 'SYNC_REJECTED' and device_id = @d::uuid", new { d = pump.DeviceId });
        Assert.Contains("INVALID_VOLUME", details, StringComparison.Ordinal);
        Assert.Contains("1500.25", details, StringComparison.Ordinal);
    }

    /// <summary>
    /// The price an offline item carries follows the same rule (B5 contract §3): a fraction rejects that item with
    /// VALIDATION_FAILED on <c>unitPricePiastres</c>, keeping what was sent for Operations.
    /// </summary>
    [Fact]
    public async Task AFractionalPriceInAnOfflineBatchRejectsThatItemOnly()
    {
        var o = new OfflineTestSupport(api.Factory);
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = (await f.Id.SeedCompanyAsync()).ToString();
        await f.CreditAsync(company, 1_000_000);
        var fraction = (await o.CaptureAsync(pump, await f.CarAsync(company), 1_000)).Json()
            .Replace("\"receiptLocale\"", "\"unitPricePiastres\":1499.5,\"receiptLocale\"", StringComparison.Ordinal);
        var whole = (await o.CaptureAsync(pump, await f.CarAsync(company), 1_000)).Json()
            .Replace("\"receiptLocale\"", "\"unitPricePiastres\":1.5e3,\"receiptLocale\"", StringComparison.Ordinal);

        var answered = await OfflineTestSupport.AnsweredAsync(await f.Pos.PosAsync("POST", "/api/v1/pos/sync", pump.DeviceToken, null,
            "{\"batchId\":\"" + Guid.NewGuid() + "\",\"pending\":0,\"items\":[" + fraction + "," + whole + "]}"));

        Assert.Equal("VALIDATION_FAILED", OfflineTestSupport.Rejection(answered, 0));
        Assert.Empty(OfflineTestSupport.Conflicts(answered, 1)); // 1.5e3 is 1500, the price in force
        var details = await f.Owner.QueryOneAsync<string>(
            "select details::text from alerts where type = 'SYNC_REJECTED' and device_id = @d::uuid", new { d = pump.DeviceId });
        Assert.Contains("\"sentValue\": \"1499.5\"", details, StringComparison.Ordinal);
        Assert.Contains("\"code\": \"VALIDATION_FAILED\"", details, StringComparison.Ordinal);
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
