using System.Net;
using System.Text;
using System.Text.Json;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Modules.Tenancy;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Fleet.FleetTestSupport;

namespace Zawed.Tests.Modules.Fleet;

/// <summary>Vehicles, RFID tags and limits: the rules of PRD §4.3–4.5 and the company's read-only view (Java <c>FleetTests</c>).</summary>
public sealed class FleetTests(ProbeApi api) : IClassFixture<ProbeApi>
{
    [Fact]
    public async Task APlateIsUniqueWhateverTheSpelling()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var company = await api.SeedCompanyAsync();
        var plate = "ق ص م " + (1000 + Random.Shared.Next(8000));
        Assert.Equal(HttpStatusCode.Created, (await api.CreateVehicleAsync(ops, company, plate)).StatusCode);

        // Same plate without spaces and with Eastern Arabic digits.
        var duplicate = await api.CreateVehicleAsync(ops, company, EasternDigits(plate.Replace(" ", "")));
        await Http.AssertErrorAsync(duplicate, 409, "PLATE_IN_USE");

        // Search folds the same way; a query with nothing a plate could contain matches nothing.
        var query = Uri.EscapeDataString(EasternDigits(plate[^4..]));
        var found = await api.GetAsync(ops, $"/api/v1/ops/vehicles?companyId={company}&q={query}");
        Assert.Equal([plate], (await found.JsonAsync()).Items("plate"));
        var none = await api.GetAsync(ops, "/api/v1/ops/vehicles?q=--");
        Assert.Equal(0, (await none.JsonAsync()).GetProperty("items").GetArrayLength());
    }

    /// <summary>
    /// QA round 1: searching ه ق ه 9036 did not find a plate registered as هـ ق ه 9036. The tatweel (U+0640) only
    /// stretches a letter, so search ignores it on both sides. The plate's identity key is not changed by this.
    /// </summary>
    [Fact]
    public async Task PlateSearchIgnoresTheTatweel()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var company = await api.SeedCompanyAsync();
        var digits = (1000 + Random.Shared.Next(8000)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var stretched = "هـ ق ه " + digits;
        var created = await api.CreateVehicleAsync(ops, company, stretched);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var plain = "ى ط ة " + (1000 + Random.Shared.Next(8000));
        Assert.Equal(HttpStatusCode.Created, (await api.CreateVehicleAsync(ops, company, plain)).StatusCode);

        Assert.Equal([stretched], await SearchAsync(ops, company, "ه ق ه " + digits));
        Assert.Equal([stretched], await SearchAsync(ops, company, "هقه"));
        Assert.Equal([stretched], await SearchAsync(ops, company, digits + " هـ"));
        // The other way: a query typed with tatweels finds a plate stored without them, and the letter variants PlateKey
        // already folds (ى/ي, ة/ه) still fold.
        Assert.Equal([plain], await SearchAsync(ops, company, "يـ ـطـ ه"));
        // A query of tatweels only has nothing a plate could contain: no match, not every vehicle.
        Assert.Empty(await SearchAsync(ops, company, "ـــ"));

        var id = Guid.Parse((await created.JsonAsync()).Text("id")!);
        Assert.Equal(stretched, await api.Owner.QueryOneAsync<string>("select plate from vehicles where id = @id", new { id }));
    }

    private async Task<List<string?>> SearchAsync(ZawedPrincipal ops, Guid company, string query)
    {
        var response = await api.GetAsync(ops, $"/api/v1/ops/vehicles?companyId={company}&q={Uri.EscapeDataString(query)}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.JsonAsync()).Items("plate");
    }

    [Fact]
    public async Task AVipCeilingIsForVipVehiclesOnlyAndHiddenFromTheCompany()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var company = await api.SeedCompanyAsync();
        await Http.AssertErrorAsync(await api.CreateVehicleAsync(ops, company, UniquePlate(), "NORMAL", 100_000), 400, "VIP_CEILING_ONLY_FOR_VIP");

        var vip = await api.CreateVehicleAsync(ops, company, UniquePlate(), "VIP", 300_000);
        Assert.Equal(HttpStatusCode.Created, vip.StatusCode);
        var body = await vip.JsonAsync();
        Assert.Equal(300_000, body.GetProperty("vipMonthlyCeilingMl").GetInt64());

        var manager = await api.SeedAsync(Role.FLEET_MANAGER, Scope.Company(company));
        var seen = await api.GetAsync(manager, $"/api/v1/company/vehicles/{body.Text("id")}");
        Assert.Equal(HttpStatusCode.OK, seen.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await seen.JsonAsync()).GetProperty("vehicle").GetProperty("vipMonthlyCeilingMl").ValueKind);
    }

    [Fact]
    public async Task ACompanySeesOnlyItsOwnVehicles()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var mine = await api.SeedCompanyAsync();
        var theirs = await api.SeedCompanyAsync();
        var myVehicle = (await (await api.CreateVehicleAsync(ops, mine, UniquePlate())).JsonAsync()).Text("id");
        var theirVehicle = (await (await api.CreateVehicleAsync(ops, theirs, UniquePlate())).JsonAsync()).Text("id");

        var accountant = await api.SeedAsync(Role.ACCOUNTANT, Scope.Company(mine));
        var ids = (await (await api.GetAsync(accountant, "/api/v1/company/vehicles?limit=100")).JsonAsync()).Items("id");
        Assert.Contains(myVehicle, ids);
        Assert.DoesNotContain(theirVehicle, ids);

        // Guessing another company's id gives the same answer as an id that does not exist.
        await Http.AssertErrorAsync(await api.GetAsync(accountant, $"/api/v1/company/vehicles/{theirVehicle}"), 404, "NOT_FOUND");
    }

    [Fact]
    public async Task ATagIsStoredEncryptedAndShownByItsLastFourOnly()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var vehicle = await api.VehicleAsync(ops);
        var uid = RandomUid();
        var colons = string.Join(':', Enumerable.Range(0, uid.Length / 2).Select(i => uid.Substring(i * 2, 2).ToLowerInvariant()));
        var issued = await api.IssueTagAsync(ops, vehicle, colons);
        var text = await issued.Content.ReadAsStringAsync();
        Assert.True(issued.StatusCode == HttpStatusCode.Created, text);
        Assert.DoesNotContain(uid, text);
        Assert.DoesNotContain(uid.ToLowerInvariant(), text);
        var body = await issued.JsonAsync();
        Assert.Equal(uid[^4..], body.Text("uidLast4"));

        var id = Guid.Parse(body.Text("id")!);
        var stored = await api.Owner.QueryOneAsync<byte[]>("select uid_encrypted from rfid_tags where id = @id", new { id });
        Assert.DoesNotContain(uid, Encoding.Latin1.GetString(stored!));
        var lookup = await api.Owner.QueryOneAsync<string>("select uid_lookup from rfid_tags where id = @id", new { id });
        Assert.NotEqual(uid, lookup, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(64, lookup!.Length);
    }

    [Fact]
    public async Task OneActiveTagPerVehicleAndALostTagIsNeverBoundAgain()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var vehicle = await api.VehicleAsync(ops);
        var first = RandomUid();
        var firstTag = (await (await api.IssueTagAsync(ops, vehicle, first)).JsonAsync()).Text("id")!;

        await Http.AssertErrorAsync(await api.IssueTagAsync(ops, vehicle, RandomUid()), 409, "VEHICLE_HAS_ACTIVE_TAG");

        Assert.Equal(HttpStatusCode.OK, (await api.TagStatusAsync(ops, firstTag, "LOST")).StatusCode);
        // Final: a lost tag cannot come back.
        await Http.AssertErrorAsync(await api.TagStatusAsync(ops, firstTag, "ACTIVE"), 409, "INVALID_TAG_TRANSITION");

        await Http.AssertErrorAsync(await api.IssueTagAsync(ops, await api.VehicleAsync(ops), first), 409, "TAG_ALREADY_BOUND");

        Assert.Equal(HttpStatusCode.Created, (await api.IssueTagAsync(ops, vehicle, RandomUid())).StatusCode);
    }

    [Fact]
    public async Task AReplacementTagInheritsTheCooldown()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var vehicle = await api.VehicleAsync(ops);
        var oldTag = Guid.Parse((await (await api.IssueTagAsync(ops, vehicle, RandomUid())).JsonAsync()).Text("id")!);
        await api.Owner.ExecuteAsync("update rfid_tags set cooldown_until = now() + interval '2 hours' where id = @oldTag", new { oldTag });
        Assert.Equal(HttpStatusCode.OK, (await api.TagStatusAsync(ops, oldTag.ToString(), "REPLACEMENT_PENDING")).StatusCode);

        var replacement = await api.IssueTagAsync(ops, vehicle, RandomUid());
        Assert.Equal(HttpStatusCode.Created, replacement.StatusCode);
        Assert.NotNull((await replacement.JsonAsync()).Text("cooldownUntil"));
        Assert.Equal("DEACTIVATED", await api.Owner.QueryOneAsync<string>("select status from rfid_tags where id = @oldTag", new { oldTag }));
    }

    [Fact]
    public async Task ALimitReplacesThePreviousOneForTheSameKindAndPeriod()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var vehicle = await api.VehicleAsync(ops);
        await SetLimitAsync(ops, vehicle, "VOLUME", "DAY", 60_000);
        await SetLimitAsync(ops, vehicle, "AMOUNT", "MONTH", 500_000);
        var replaced = await SetLimitAsync(ops, vehicle, "VOLUME", "DAY", 40_000);

        var limits = (await replaced.JsonAsync()).EnumerateArray().ToList();
        Assert.Equal(2, limits.Count);
        Assert.Contains(limits, l => l.Text("kind") == "VOLUME" && l.GetProperty("maxValue").GetInt64() == 40_000);
        Assert.Equal(1L, await api.Owner.QueryOneAsync<long>(
            "select count(*) from limit_rules where vehicle_id = @id and not active", new { id = Guid.Parse(vehicle) }));

        var limitId = limits[0].Text("id");
        Assert.Equal(HttpStatusCode.NoContent,
            (await api.SendAsync(ops, HttpMethod.Delete, $"/api/v1/ops/vehicles/{vehicle}/limits/{limitId}")).StatusCode);
        var detail = await api.GetAsync(ops, $"/api/v1/ops/vehicles/{vehicle}");
        Assert.Equal(1, (await detail.JsonAsync()).GetProperty("limits").GetArrayLength());
    }

    [Fact]
    public async Task DecommissioningDeactivatesTagsAndFreesThePlate()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var company = await api.SeedCompanyAsync();
        var plate = UniquePlate();
        var vehicle = (await (await api.CreateVehicleAsync(ops, company, plate)).JsonAsync()).Text("id")!;
        await api.IssueTagAsync(ops, vehicle, RandomUid());

        var done = await api.PostAsync(ops, $"/api/v1/ops/vehicles/{vehicle}/status", """{"status":"DECOMMISSIONED","reason":"Sold"}""");
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Equal(0L, await api.Owner.QueryOneAsync<long>(
            "select count(*) from rfid_tags where vehicle_id = @id and status = 'ACTIVE'", new { id = Guid.Parse(vehicle) }));
        await Http.AssertErrorAsync(await api.PostAsync(ops, $"/api/v1/ops/vehicles/{vehicle}/status", """{"status":"ACTIVE","reason":"Oops"}"""),
            409, "VEHICLE_DECOMMISSIONED");
        Assert.Equal(HttpStatusCode.Created, (await api.CreateVehicleAsync(ops, company, plate)).StatusCode);
    }

    [Fact]
    public async Task ADepartmentMustBelongToTheVehiclesCompany()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var company = await api.SeedCompanyAsync();
        var other = await api.SeedCompanyAsync();
        var department = (await (await api.PostAsync(ops, $"/api/v1/ops/companies/{other}/departments", """{"name":"Logistics"}"""))
            .JsonAsync()).Text("id");

        var response = await api.CreateVehicleAsync(ops, company, UniquePlate(), departmentId: department);
        var refused = await Http.AssertErrorAsync(response, 400, "DEPARTMENT_NOT_FOUND");
        Assert.Equal("departmentId", refused.Text("field"));
    }

    [Fact]
    public async Task EveryChangeIsAudited()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var company = await api.SeedCompanyAsync();
        var vehicle = (await (await api.CreateVehicleAsync(ops, company, UniquePlate())).JsonAsync()).Text("id")!;
        await api.IssueTagAsync(ops, vehicle, RandomUid());
        await SetLimitAsync(ops, vehicle, "VOLUME", "WEEK", 200_000);
        var actions = await api.Owner.QueryAsync<string>("select action from audit_log where scope_id = @company order by occurred_at", new { company });
        Assert.Equal(["VEHICLE_CREATED", "TAG_ISSUED", "LIMIT_SET"], actions);
    }

    // ---------------------------------------------------------------------------------------------- concurrency

    [Fact]
    public async Task TheSameTagRacingOntoTwoVehiclesIsBoundOnce()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var uid = RandomUid();
        List<string> vehicles = [await api.VehicleAsync(ops), await api.VehicleAsync(ops)];
        var statuses = await RaceAsync(2, async i => (int)(await api.IssueTagAsync(ops, vehicles[i], uid)).StatusCode);
        Assert.Equal([201, 409], statuses.Order());
    }

    [Fact]
    public async Task TwoTagsRacingOntoOneVehicleLeaveOneActive()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var vehicle = await api.VehicleAsync(ops);
        var statuses = await RaceAsync(2, async _ => (int)(await api.IssueTagAsync(ops, vehicle, RandomUid())).StatusCode);
        Assert.Equal([201, 409], statuses.Order());
        Assert.Equal(1L, await api.Owner.QueryOneAsync<long>(
            "select count(*) from rfid_tags where vehicle_id = @id and status = 'ACTIVE'", new { id = Guid.Parse(vehicle) }));
    }

    [Fact]
    public async Task RacingLimitChangesLeaveOneActiveRule()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var vehicle = await api.VehicleAsync(ops);
        var statuses = await RaceAsync(4, async i => (int)(await api.PutAsync(ops, $"/api/v1/ops/vehicles/{vehicle}/limits",
            $$"""{"kind":"VOLUME","period":"DAY","maxValue":{{10_000 + i}}}""")).StatusCode);
        Assert.All(statuses, s => Assert.Equal(200, s));
        Assert.Equal(1L, await api.Owner.QueryOneAsync<long>(
            "select count(*) from limit_rules where vehicle_id = @id and kind = 'VOLUME' and period = 'DAY' and active",
            new { id = Guid.Parse(vehicle) }));
    }

    // ------------------------------------------------------------------------------------------------------
    // Beyond the Java class: the B2 rules it did not pin.

    [Fact]
    public async Task ACompanyUserCannotChangeTheFleetAndUnknownIdsAre404()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var company = await api.SeedCompanyAsync();
        var manager = await api.SeedAsync(Role.FLEET_MANAGER, Scope.Company(company));
        await Http.AssertErrorAsync(await api.CreateVehicleAsync(manager, company, UniquePlate()), 403, "FORBIDDEN");
        await Http.AssertErrorAsync(await api.GetAsync(ops, $"/api/v1/ops/vehicles/{Guid.CreateVersion7()}"), 404, "NOT_FOUND");
        await Http.AssertErrorAsync(await api.IssueTagAsync(ops, Guid.CreateVersion7().ToString(), RandomUid()), 404, "NOT_FOUND");
        await Http.AssertErrorAsync(await api.TagStatusAsync(ops, Guid.CreateVersion7().ToString(), "LOST"), 404, "NOT_FOUND");
        var vehicle = await api.VehicleAsync(ops);
        await Http.AssertErrorAsync(await api.SendAsync(ops, HttpMethod.Delete, $"/api/v1/ops/vehicles/{vehicle}/limits/{Guid.CreateVersion7()}"),
            404, "NOT_FOUND");
        var unknownCompany = await api.CreateVehicleAsync(ops, Guid.CreateVersion7(), UniquePlate());
        Assert.Equal("companyId", (await Http.AssertErrorAsync(unknownCompany, 400, "COMPANY_NOT_FOUND")).Text("field"));
        Assert.Equal("uid", (await Http.AssertErrorAsync(await api.IssueTagAsync(ops, vehicle, "12:34"), 400, "INVALID_TAG_UID")).Text("field"));
        var missing = await api.PostAsync(ops, "/api/v1/ops/vehicles", $$$"""{"companyId":"{{{company}}}","vehicle":{"plate":" ","make":"M"}}""");
        var fields = (await Http.AssertErrorAsync(missing, 400, "VALIDATION_FAILED")).GetProperty("fields").EnumerateArray()
            .Select(f => f.Text("field") + ":" + f.Text("code")).ToHashSet();
        foreach (var expected in new[] { "vehicle.plate:NOT_BLANK", "vehicle.model:NOT_BLANK", "vehicle.fuelType:NOT_NULL",
            "vehicle.tankCapacityMl:NOT_NULL", "vehicle.tier:NOT_NULL" })
        {
            Assert.Contains(expected, fields);
        }
    }

    [Fact]
    public async Task AnUpdateIsAuditedWithWhatChangedAndASuspendedVehicleGetsNoTag()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var vehicle = await api.VehicleAsync(ops);
        var update = await api.PutAsync(ops, $"/api/v1/ops/vehicles/{vehicle}",
            """{"plate":"NEW 9","make":"Isuzu","model":"NPR","fuelType":"DIESEL","tankCapacityMl":90000,"tier":"VIP","vipMonthlyCeilingMl":5}""");
        Assert.True(update.StatusCode == HttpStatusCode.OK, await update.Content.ReadAsStringAsync());
        var metadata = await api.Owner.QueryOneAsync<string>(
            "select metadata::text from audit_log where action = 'VEHICLE_UPDATED' and entity_id = @id", new { id = Guid.Parse(vehicle) });
        using (var json = JsonDocument.Parse(metadata!))
        {
            Assert.Equal("DIESEL", json.RootElement.GetProperty("fuelType").Text("to"));
            Assert.Equal("VIP", json.RootElement.GetProperty("tier").Text("to"));
            Assert.Equal("NEW 9", json.RootElement.GetProperty("plate").Text("to"));
        }
        var suspended = await api.PostAsync(ops, $"/api/v1/ops/vehicles/{vehicle}/status", """{"status":"SUSPENDED","reason":"  check  "}""");
        Assert.Equal("SUSPENDED", (await suspended.JsonAsync()).Text("status"));
        Assert.Equal("check", await api.Owner.QueryOneAsync<string>(
            "select metadata->>'reason' from audit_log where action = 'VEHICLE_STATUS_CHANGED' and entity_id = @id", new { id = Guid.Parse(vehicle) }));
        await Http.AssertErrorAsync(await api.IssueTagAsync(ops, vehicle, RandomUid()), 409, "VEHICLE_NOT_ACTIVE");
    }

    private async Task<HttpResponseMessage> SetLimitAsync(ZawedPrincipal ops, string vehicle, string kind, string period, long max)
    {
        var response = await api.PutAsync(ops, $"/api/v1/ops/vehicles/{vehicle}/limits",
            JsonSerializer.Serialize(new { kind, period, maxValue = max }));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return response;
    }
}
