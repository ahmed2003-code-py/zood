using System.Text;
using Zawed.Api.Modules.Fueling;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Fueling.FuelingTestSupport;

namespace Zawed.Tests.Modules.Fueling;

/// <summary>Fuelings as CSV (Java <c>FuelingExportsTests</c>): each audience gets exactly its own rows, in a file a spreadsheet reads correctly.</summary>
public sealed class FuelingExportsTests(SharedApi api) : IClassFixture<SharedApi>
{
    private static readonly string Today =
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Activity.Cairo).DateTime).ToString("yyyy-MM-dd");

    private readonly FuelingTestSupport f = new(api.Factory);

    [Fact]
    public async Task EachAudienceExportsOnlyItsOwnFuelings()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var mine = await f.CompanyAsync();
        var theirs = await f.CompanyAsync();
        await f.CreditAsync(mine, 100_000);
        await f.CreditAsync(theirs, 100_000);
        var myRef = (await BodyAsync(await f.FuelAsync(pump, await f.CarAsync(mine), 20_000), 201)).GetProperty("reference").GetString()!;
        var theirRef = (await BodyAsync(await f.FuelAsync(pump, await f.CarAsync(theirs), 10_000), 201)).GetProperty("reference").GetString()!;

        var accountant = await f.SignInAsync(Role.ACCOUNTANT, Scope.Company(Guid.Parse(mine)));
        var file = await f.GetAsync($"/api/v1/company/fuelings/export?from={Today}&to={Today}", accountant);
        Assert.Equal(200, (int)file.StatusCode);
        Assert.StartsWith("text/csv", file.Content.Headers.ContentType!.ToString(), StringComparison.Ordinal);
        Assert.Contains("attachment", file.Content.Headers.ContentDisposition!.ToString(), StringComparison.Ordinal);
        Assert.Contains($"zawed-fuelings-{Today}-{Today}.csv", file.Content.Headers.ContentDisposition!.ToString(), StringComparison.Ordinal);
        Assert.Contains("no-store", file.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        var body = await file.Content.ReadAsByteArrayAsync();
        Assert.Equal(0xEF, body[0]); // UTF-8 byte-order mark for Excel
        var text = Encoding.UTF8.GetString(body);
        Assert.Contains("Reference,Time (Cairo)", text, StringComparison.Ordinal);
        Assert.Contains(myRef, text, StringComparison.Ordinal);
        Assert.DoesNotContain(theirRef, text, StringComparison.Ordinal);
        Assert.Contains(",20.00,15.00,300.00,", text, StringComparison.Ordinal); // 20 L at 15.00 = 300.00

        var station = await f.SignInAsync(Role.STATION_MANAGER, new Scope(ScopeType.OPERATOR, Guid.Parse(pump.OperatorId)));
        var atStation = Encoding.UTF8.GetString(await (await f.GetAsync(
            $"/api/v1/station/fuelings/export?from={Today}&to={Today}&lang=ar", station)).Content.ReadAsByteArrayAsync());
        Assert.Contains("المرجع", atStation, StringComparison.Ordinal);
        Assert.Contains(myRef, atStation, StringComparison.Ordinal);
        Assert.Contains(theirRef, atStation, StringComparison.Ordinal);
        Assert.Contains("شركة اختبار", atStation, StringComparison.Ordinal); // the Arabic name in the Arabic file
    }

    [Fact]
    public async Task TheRangeIsRequiredAndAtMostAYear()
    {
        var ops = await f.OpsAsync();
        var missing = await Http.AssertErrorAsync(await f.GetAsync("/api/v1/ops/fuelings/export", ops), 400, "VALIDATION_FAILED");
        Assert.Equal("from", missing.GetProperty("field").GetString());
        var twoYears = DateOnly.Parse(Today).AddYears(-2).ToString("yyyy-MM-dd");
        var tooLong = await Http.AssertErrorAsync(await f.GetAsync($"/api/v1/ops/fuelings/export?from={twoYears}&to={Today}", ops), 400,
            "EXPORT_RANGE_TOO_LONG");
        Assert.Equal(366, tooLong.GetProperty("params").GetProperty("maxDays").GetInt64());
        var backwards = DateOnly.Parse(Today).AddDays(-2).ToString("yyyy-MM-dd");
        Assert.Equal("to", (await Http.AssertErrorAsync(await f.GetAsync($"/api/v1/ops/fuelings/export?from={Today}&to={backwards}", ops), 400,
            "VALIDATION_FAILED")).GetProperty("field").GetString());
    }

    [Fact]
    public void AmountsAndVolumesAreWrittenAsPlainDecimals()
    {
        Assert.Equal("315.00", FuelingExports.Decimal(31_500, 100));
        Assert.Equal("20.00", FuelingExports.Decimal(20_000, 1000));
        Assert.Equal("-15.50", FuelingExports.Decimal(-1_550, 100));
        Assert.Equal("0.05", FuelingExports.Decimal(5, 100));
        Assert.Equal("1.23", FuelingExports.Decimal(1_239, 1000)); // millilitres truncated to centilitres, as Java did
    }
}
