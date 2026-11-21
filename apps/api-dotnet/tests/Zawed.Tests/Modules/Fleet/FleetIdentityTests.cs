using System.Net;
using Npgsql;
using Zawed.Api.Modules.Fleet;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Modules.Tenancy;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Fleet.FleetTestSupport;

namespace Zawed.Tests.Modules.Fleet;

/// <summary>
/// Vehicle identity (V14, ADR 0024; Java <c>FleetIdentityTests</c>): canonical Egyptian plate keys that never merge two
/// different plates, a fleet code unique per company, a tank ceiling, and identity columns no one can rewrite.
/// </summary>
public sealed class FleetIdentityTests(ProbeApi api) : IClassFixture<ProbeApi>
{
    [Fact]
    public async Task AnEgyptianPlateIsOnePlateWhicheverOrderItIsTypedIn()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var digits = (1000 + Random.Shared.Next(8999)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var letters = RandomLetters();
        var first = await api.CreateVehicleAsync(ops, await api.SeedCompanyAsync(), digits + " " + letters);
        Assert.True(first.StatusCode == HttpStatusCode.Created, await first.Content.ReadAsStringAsync());
        // Letters first, Arabic digits, no spaces: the same plate.
        var reordered = await api.CreateVehicleAsync(ops, await api.SeedCompanyAsync(), letters.Replace(" ", "") + EasternDigits(digits));
        await Http.AssertErrorAsync(reordered, 409, "PLATE_IN_USE");
        // The plate is shown exactly as registered.
        Assert.Equal(digits + " " + letters, (await first.JsonAsync()).Text("plate"));
    }

    [Fact]
    public void PlatesOfAnotherShapeAreNeverMergedByTheCanonicalForm()
    {
        Assert.Equal("1234طبع", PlateKey.Of("١٢٣٤ ط ب ع"));
        Assert.Equal(PlateKey.Of("ط ب ع 1234"), PlateKey.Of("١٢٣٤ ط ب ع"));
        Assert.NotEqual(PlateKey.Of("AB12CD"), PlateKey.Of("ABCD12"));
        Assert.Equal("12اب", PlateKey.Of("أ ب 12"));
        Assert.Equal("12AB34", PlateKey.Canonical("12AB34"));
    }

    [Fact]
    public async Task TheKeyIsTheOneTheDatabaseComputesForStoredPlates()
    {
        // V14's plate_key_canonical reorders keys written before it; a key from PlateKey.Of is already canonical.
        foreach (var plate in new[] { "ط ب ع 1234", "أ ب 12", "AB12CD", "هـ ق ه 9036", "ﻻ 5" })
        {
            var key = PlateKey.Of(plate);
            Assert.Equal(key, await api.Owner.QueryOneAsync<string>("select plate_key_canonical(@key)", new { key }));
        }
    }

    [Fact]
    public async Task AFleetCodeIsUniquePerCompanyAmongVehiclesInService()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var company = await api.SeedCompanyAsync();
        var code = "TRK-" + Guid.NewGuid().ToString()[..6];
        var first = await api.CreateVehicleAsync(ops, company, UniquePlate(), fleetCode: code);
        Assert.Equal(code, (await first.JsonAsync()).Text("fleetCode"));
        var clash = await api.CreateVehicleAsync(ops, company, UniquePlate(), fleetCode: code.ToLowerInvariant());
        Assert.Equal("fleetCode", (await Http.AssertErrorAsync(clash, 409, "FLEET_CODE_IN_USE")).Text("field"));
        Assert.Equal(HttpStatusCode.Created, (await api.CreateVehicleAsync(ops, await api.SeedCompanyAsync(), UniquePlate(), fleetCode: code)).StatusCode);

        var vehicle = (await first.JsonAsync()).Text("id");
        await api.PostAsync(ops, $"/api/v1/ops/vehicles/{vehicle}/status", """{"status":"DECOMMISSIONED","reason":"sold"}""");
        Assert.Equal(HttpStatusCode.Created, (await api.CreateVehicleAsync(ops, company, UniquePlate(), fleetCode: code)).StatusCode);
    }

    [Fact]
    public async Task ATankAbove1500LitresAndAnImplausibleYearAreRefused()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var company = await api.SeedCompanyAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await api.CreateVehicleAsync(ops, company, UniquePlate(), tankMl: 1_600_000)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await api.CreateVehicleAsync(ops, company, UniquePlate(), tankMl: 1_500_000)).StatusCode);
        var future = await api.CreateVehicleAsync(ops, company, UniquePlate(), year: 2099);
        Assert.Equal("year", (await Http.AssertErrorAsync(future, 400, "INVALID_YEAR")).Text("field"));
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            api.Owner.ExecuteAsync("update vehicles set tank_capacity_ml = 2000000 where company_id = @company", new { company }));
        Assert.Contains("vehicles_tank_capacity_ceiling", ex.Message);
    }

    [Fact]
    public async Task IdentityColumnsCannotBeRewrittenEvenByTheOwner()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var company = await api.SeedCompanyAsync();
        var vehicle = Guid.Parse((await (await api.CreateVehicleAsync(ops, company, UniquePlate())).JsonAsync()).Text("id")!);
        var other = Guid.Parse((await (await api.CreateVehicleAsync(ops, company, UniquePlate())).JsonAsync()).Text("id")!);
        var tag = Guid.Parse((await (await api.IssueTagAsync(ops, vehicle.ToString(), RandomUid())).JsonAsync()).Text("id")!);

        Assert.Contains("never change", (await Assert.ThrowsAsync<PostgresException>(() =>
            api.Owner.ExecuteAsync("update rfid_tags set vehicle_id = @other where id = @tag", new { other, tag }))).Message);
        Assert.Contains("never change", (await Assert.ThrowsAsync<PostgresException>(() =>
            api.Owner.ExecuteAsync("update rfid_tags set uid_last4 = 'FFFF' where id = @tag", new { tag }))).Message);
        var newCompany = await api.SeedCompanyAsync();
        Assert.Contains("never moves", (await Assert.ThrowsAsync<PostgresException>(() =>
            api.Owner.ExecuteAsync("update vehicles set company_id = @newCompany where id = @vehicle", new { newCompany, vehicle }))).Message);
        // Status changes, the lifecycle, still work.
        Assert.Equal(1, await api.Owner.ExecuteAsync("update rfid_tags set status = 'LOST' where id = @tag", new { tag }));
    }

    [Fact]
    public async Task RacingRegistrationsOfOnePlateCreateOneVehicle()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var plate = EgyptianPlate();
        List<Guid> companies = [await api.SeedCompanyAsync(), await api.SeedCompanyAsync(), await api.SeedCompanyAsync()];
        var statuses = await RaceAsync(3, async i => (int)(await api.CreateVehicleAsync(ops, companies[i], plate)).StatusCode);
        Assert.Equal([201, 409, 409], statuses.Order());
        Assert.Equal(1L, await api.Owner.QueryOneAsync<long>("select count(*) from vehicles where plate = @plate", new { plate }));
    }

    [Fact]
    public async Task AFleetCodeWithAControlCharacterIsABadRequestNotAServerError()
    {
        var ops = await api.SeedAsync(Role.OPS);
        // U+0001; a tab would be whitespace and is normalised to a space instead.
        var response = await api.CreateVehicleAsync(ops, await api.SeedCompanyAsync(), UniquePlate(), fleetCode: "TRK\u0001001");
        await Http.AssertErrorAsync(response, 400, "INVALID_FLEET_CODE");
    }
}
