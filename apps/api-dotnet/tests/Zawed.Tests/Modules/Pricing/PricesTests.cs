using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Zawed.Api.Modules.Pricing.Contracts;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Domain;
using Zawed.Tests.Modules.Tenancy;
using Zawed.Tests.Support;

namespace Zawed.Tests.Modules.Pricing;

/// <summary>Effective-dated prices: a price is never backdated, and the latest to take effect wins (Java <c>PricesTests</c>).</summary>
public sealed class PricesTests(ProbeApi api) : IClassFixture<ProbeApi>
{
    [Fact]
    public async Task APriceCannotTakeEffectInThePast()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var response = await ScheduleAsync(ops, "GAS_95", null, 2000, DateTimeOffset.UtcNow.AddHours(-1));
        var refused = await Http.AssertErrorAsync(response, 400, "PRICE_BACKDATED");
        Assert.Equal("effectiveFrom", refused.Text("field"));

        // Inside the clock tolerance it is accepted, but stored as taking effect now, not earlier.
        var requested = DateTimeOffset.UtcNow.AddSeconds(-30);
        var accepted = await ScheduleAsync(ops, "GAS_95", null, 2000, requested);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.True(Instant((await accepted.JsonAsync()).Text("effectiveFrom")) > requested.AddSeconds(25));
    }

    [Fact]
    public async Task TheLatestPriceToTakeEffectWinsBetweenNationalAndStation()
    {
        // CNG is used by this test alone, so other tests' prices do not interfere.
        var ops = await api.SeedAsync(Role.OPS);
        var station = await api.SeedStationAsync();
        var now = DateTimeOffset.UtcNow;
        var @base = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero).AddDays(10);
        Assert.Equal(HttpStatusCode.Created, (await ScheduleAsync(ops, "CNG", null, 700, @base)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ScheduleAsync(ops, "CNG", station, 750, @base.AddDays(1))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ScheduleAsync(ops, "CNG", null, 800, @base.AddDays(2))).StatusCode);

        Assert.Equal(700, await PriceAtAsync(station, @base.AddHours(12)));
        Assert.Equal(750, await PriceAtAsync(station, @base.AddHours(36)));
        // A national change after the override supersedes it.
        Assert.Equal(800, await PriceAtAsync(station, @base.AddHours(60)));
        // Another station never saw the override.
        Assert.Equal(700, await PriceAtAsync(Guid.CreateVersion7(), @base.AddHours(36)));

        var same = await ScheduleAsync(ops, "CNG", null, 900, @base);
        var refused = await Http.AssertErrorAsync(same, 409, "PRICE_EXISTS");
        Assert.Equal("effectiveFrom", refused.Text("field"));

        // What a POS needs to apply the rule itself until a moment: what is in force, then the changes, oldest first.
        var entries = await InPlatformScopeAsync(book => book.EntriesForAsync(station, FuelType.CNG, @base.AddHours(12), @base.AddDays(3)));
        Assert.Equal([(null, 700L), (station, 750L), (null, 800L)], entries.Select(e => (e.StationId, e.PricePiastresPerLitre)));
    }

    [Fact]
    public async Task SchedulingIsAuditedAndPricesAreNeverEdited()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var tooFar = await ScheduleAsync(ops, "GAS_80", null, 1300, DateTimeOffset.UtcNow.AddDays(400));
        await Http.AssertErrorAsync(tooFar, 400, "PRICE_TOO_FAR_AHEAD");
        var created = await ScheduleAsync(ops, "GAS_80", null, 1300, DateTimeOffset.UtcNow.AddDays(5));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = Guid.Parse((await created.JsonAsync()).Text("id")!);
        Assert.Equal(1L, await api.Owner.QueryOneAsync<long>(
            "select count(*) from audit_log where action = 'PRICE_SCHEDULED' and entity_id = @id and scope_type = 'PLATFORM'", new { id }));
        Assert.False(await api.Owner.QueryOneAsync<bool>(
            "select has_table_privilege('zawed_app', 'fuel_prices', 'UPDATE') or has_table_privilege('zawed_app', 'fuel_prices', 'DELETE')"));
    }

    // ------------------------------------------------------------------------------------------------------
    // Beyond the Java class: the rest of the B2 contract for prices.

    [Fact]
    public async Task CurrentPricesAndTheHistoryAreFilteredAndAnUnknownStationIsRefused()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var station = await api.SeedStationAsync("{DIESEL}");
        Assert.Equal(HttpStatusCode.Created, (await ScheduleAsync(ops, "DIESEL", null, 1500, null)).StatusCode);
        var overridden = await ScheduleAsync(ops, "DIESEL", station, 1550, null, note: "  Remote   site ");
        Assert.Equal(HttpStatusCode.Created, overridden.StatusCode);
        var entry = await overridden.JsonAsync();
        Assert.Equal("Remote site", entry.Text("note"));
        Assert.Equal(station.ToString(), entry.Text("stationId"));
        Assert.Equal(1550, entry.GetProperty("pricePiastresPerLitre").GetInt64());

        var national = await (await api.GetAsync(ops, "/api/v1/ops/prices/current")).JsonAsync();
        var nationalDiesel = national.EnumerateArray().Single(p => p.Text("fuelType") == "DIESEL");
        Assert.Equal(JsonNull, nationalDiesel.GetProperty("stationId").ValueKind);
        var atStation = await (await api.GetAsync(ops, $"/api/v1/ops/prices/current?stationId={station}")).JsonAsync();
        Assert.Equal(1550, atStation.EnumerateArray().Single(p => p.Text("fuelType") == "DIESEL").GetProperty("pricePiastresPerLitre").GetInt64());
        // Fuels are listed in the declared order.
        var fuels = atStation.EnumerateArray().Select(p => p.Text("fuelType")).ToList();
        Assert.Equal(fuels.OrderBy(f => Enum.Parse<FuelType>(f!)), fuels);

        var history = await (await api.GetAsync(ops, $"/api/v1/ops/prices?fuelType=DIESEL&stationId={station}")).JsonAsync();
        Assert.Equal([entry.Text("id")], history.Items("id"));
        var nationalOnly = await (await api.GetAsync(ops, "/api/v1/ops/prices?fuelType=DIESEL&nationalOnly=true&limit=100")).JsonAsync();
        Assert.All(nationalOnly.GetProperty("items").EnumerateArray(), p => Assert.Equal(JsonNull, p.GetProperty("stationId").ValueKind));

        await Http.AssertErrorAsync(await api.GetAsync(ops, $"/api/v1/ops/prices/current?stationId={Guid.CreateVersion7()}"), 404, "NOT_FOUND");
        var unknown = await Http.AssertErrorAsync(await ScheduleAsync(ops, "DIESEL", Guid.CreateVersion7(), 1500, null), 400, "STATION_NOT_FOUND");
        Assert.Equal("stationId", unknown.Text("field"));
    }

    [Fact]
    public async Task OnlyPricesManageSchedulesAndTheBodyIsValidated()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var fleet = await api.SeedAsync(Role.FLEET_MANAGER);
        await Http.AssertErrorAsync(await ScheduleAsync(fleet, "GAS_92", null, 1000, null), 403, "FORBIDDEN");
        await Http.AssertErrorAsync(await api.GetAsync(fleet, "/api/v1/ops/prices/current"), 403, "FORBIDDEN");
        await MasterDataApi.AssertInvalidAsync(await api.PostAsync(ops, "/api/v1/ops/prices", """{"fuelType":"GAS_92","pricePiastres":0}"""), "pricePiastres");
        await MasterDataApi.AssertInvalidAsync(await api.PostAsync(ops, "/api/v1/ops/prices", """{"fuelType":"GAS_92","pricePiastres":100001}"""), "pricePiastres");
        await MasterDataApi.AssertInvalidAsync(await api.PostAsync(ops, "/api/v1/ops/prices", """{"pricePiastres":1000}"""), "fuelType");
        var fraction = await Http.AssertErrorAsync(await api.PostAsync(ops, "/api/v1/ops/prices", """{"fuelType":"GAS_92","pricePiastres":10.5}"""),
            400, "VALIDATION_FAILED");
        Assert.Equal("INTEGER_REQUIRED", fraction.GetProperty("fields")[0].GetProperty("code").GetString());
    }

    private const System.Text.Json.JsonValueKind JsonNull = System.Text.Json.JsonValueKind.Null;

    private static DateTimeOffset Instant(string? value) => DateTimeOffset.Parse(value!, CultureInfo.InvariantCulture);

    private async Task<long> PriceAtAsync(Guid station, DateTimeOffset at) =>
        (await InPlatformScopeAsync(book => book.PriceAtAsync(station, FuelType.CNG, at)))!.PricePiastresPerLitre;

    /// <summary>The price book in a platform-scoped transaction (the Java test's <c>DbScope.runAs(PLATFORM)</c>).</summary>
    private async Task<T> InPlatformScopeAsync<T>(Func<IPriceBook, Task<T>> read)
    {
        await using var services = api.Factory.Services.CreateAsyncScope();
        var session = services.ServiceProvider.GetRequiredService<DbSession>();
        var ops = new ZawedPrincipal(Guid.CreateVersion7(), Guid.CreateVersion7(), Role.OPS, Scope.Platform, false);
        await using var tx = await services.ServiceProvider.GetRequiredService<ITransactions>().BeginAsync(DbScopeContext.ForPrincipal(ops));
        session.Attach(tx);
        try
        {
            return await read(services.ServiceProvider.GetRequiredService<IPriceBook>());
        }
        finally
        {
            session.Detach();
        }
    }

    private Task<HttpResponseMessage> ScheduleAsync(ZawedPrincipal user, string fuel, Guid? station, long price, DateTimeOffset? from, string? note = null)
    {
        var parts = new List<string> { $"\"fuelType\":\"{fuel}\"", $"\"pricePiastres\":{price}" };
        if (station is not null) parts.Add($"\"stationId\":\"{station}\"");
        if (from is not null) parts.Add($"\"effectiveFrom\":\"{from.Value.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'}\"");
        if (note is not null) parts.Add($"\"note\":\"{note}\"");
        return api.PostAsync(user, "/api/v1/ops/prices", "{" + string.Join(',', parts) + "}");
    }
}
