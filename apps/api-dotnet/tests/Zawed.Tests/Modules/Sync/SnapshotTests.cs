using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Zawed.Api.Modules.Fleet;
using Zawed.Api.Platform.Crypto;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Modules.Fueling;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Fueling.FuelingTestSupport;

namespace Zawed.Tests.Modules.Sync;

/// <summary>
/// The POS snapshot and the sync monitor (B5 contract §1, §2, §5; Java <c>SnapshotTests</c>): what a device may know to work
/// offline, and what it must never be given.
/// </summary>
public sealed class SnapshotTests(SharedApi api) : IClassFixture<SharedApi>
{
    private readonly OfflineTestSupport o = new(api.Factory);

    private FuelingTestSupport F => o.F;

    [Fact]
    public async Task TheSnapshotLetsTheDeviceRecogniseATagWithoutEverHoldingItsUid()
    {
        var pump = await F.PumpAsync();
        await F.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await F.CompanyAsync();
        await F.CreditAsync(company, 123_456);
        var car = await F.CarAsync(company);
        Assert.Equal(200, (int)(await F.Id.AuthedAsync("PUT", $"/api/v1/ops/vehicles/{car.VehicleId}/limits", await F.OpsAsync(),
            """{"kind":"VOLUME","period":"DAY","maxValue":50000}""")).StatusCode);
        Assert.Equal(201, (int)(await F.FuelAsync(pump, car, 10_000)).StatusCode);

        var response = await SnapshotAsync(pump.DeviceToken);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        var text = await response.Content.ReadAsStringAsync();
        var body = await BodyAsync(response, 200);

        // The tag, by digest only.
        Assert.DoesNotContain(car.TagUid, text, StringComparison.Ordinal);
        Assert.DoesNotContain(car.TagUid.ToLowerInvariant(), text, StringComparison.Ordinal);
        var digest = Digest(body.GetProperty("tagKey").GetString()!, car.TagUid);
        var tags = body.GetProperty("tags").EnumerateArray().Where(t => t.GetProperty("digest").GetString() == digest).ToList();
        Assert.Equal([car.VehicleId], tags.Select(t => t.GetProperty("vehicleId").GetString()));
        Assert.NotEqual(JsonValueKind.Null, tags[0].GetProperty("cooldownUntil").ValueKind); // the cooldown the fueling started

        // The wallet as a word and a capped allowance, never the balance.
        var wallet = CompanyOf(body, company);
        Assert.Equal("AVAILABLE", wallet.GetProperty("walletStatus").GetString());
        // 1,234.56 − 150.00 = 1,084.56 EGP, shown as 1,000: in 100 EGP steps, never to the piastre.
        Assert.Equal(100_000, wallet.GetProperty("offlineAllowancePiastres").GetInt64());
        Assert.DoesNotContain("balance", text, StringComparison.Ordinal);
        Assert.DoesNotContain("108456", text, StringComparison.Ordinal);

        // The day limit with what is already used.
        var limit = body.GetProperty("limits").EnumerateArray().Single(l => l.GetProperty("vehicleId").GetString() == car.VehicleId);
        Assert.Equal(40_000, limit.GetProperty("remaining").GetInt64()); // 50 L limit, 10 L used
        Assert.False(limit.TryGetProperty("used", out _));
        Assert.False(limit.TryGetProperty("maxValue", out _));

        // Prices, the station, the switches, and nothing about agents.
        Assert.Contains(Price, body.GetProperty("prices").EnumerateArray().Where(p => p.GetProperty("fuelType").GetString() == "GAS_92")
            .Select(p => p.GetProperty("pricePiastresPerLitre").GetInt64()));
        Assert.Equal(pump.StationId, body.GetProperty("station").GetProperty("id").GetString());
        Assert.False(body.GetProperty("vipEnabled").GetBoolean());
        Assert.Equal(1, body.GetProperty("contract").GetInt32());
        Assert.DoesNotContain("pin", text, StringComparison.Ordinal);
        Assert.DoesNotContain(pump.WorkerId, text, StringComparison.Ordinal);
        Assert.DoesNotContain("tagId", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSnapshotHasTheJavaShape()
    {
        // Byte for byte what the Kotlin POS parses: the members and their order as the Java records declare them.
        var pump = await F.PumpAsync();
        await F.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await F.CompanyAsync();
        var car = await F.CarAsync(company);
        Assert.Equal(200, (int)(await F.Id.AuthedAsync("PUT", $"/api/v1/ops/vehicles/{car.VehicleId}/limits", await F.OpsAsync(),
            """{"kind":"AMOUNT","period":"WEEK","maxValue":90000}""")).StatusCode);

        var body = await BodyAsync(await SnapshotAsync(pump.DeviceToken), 200);

        static string[] Names(JsonElement e) => e.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["serverTime", "contract", "vipEnabled", "station", "prices", "tagKey", "tags", "vehicles", "limits", "companies"], Names(body));
        Assert.Equal(["id", "nameAr", "nameEn", "timeZone", "fuelTypes", "open"], Names(body.GetProperty("station")));
        Assert.Equal(["GAS_92", "DIESEL"], body.GetProperty("station").GetProperty("fuelTypes").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal("Africa/Cairo", body.GetProperty("station").GetProperty("timeZone").GetString());
        Assert.Equal(["fuelType", "national", "pricePiastresPerLitre", "effectiveFrom"], Names(body.GetProperty("prices")[0]));
        var tag = body.GetProperty("tags").EnumerateArray().Single(t => t.GetProperty("vehicleId").GetString() == car.VehicleId);
        Assert.Equal(["digest", "vehicleId", "status", "cooldownUntil"], Names(tag));
        Assert.Matches("^[0-9a-f]{8}$", tag.GetProperty("digest").GetString());
        Assert.Equal("ACTIVE", tag.GetProperty("status").GetString());
        var vehicle = body.GetProperty("vehicles").EnumerateArray().Single(v => v.GetProperty("id").GetString() == car.VehicleId);
        Assert.Equal(["id", "companyId", "plate", "fuelType", "vip", "tankCapacityMl", "active"], Names(vehicle));
        var limit = body.GetProperty("limits").EnumerateArray().Single(l => l.GetProperty("vehicleId").GetString() == car.VehicleId);
        Assert.Equal(["vehicleId", "kind", "period", "remaining", "periodStart"], Names(limit));
        Assert.Equal(90_000, limit.GetProperty("remaining").GetInt64());
        Assert.Equal(["id", "nameAr", "nameEn", "active", "cooldownMinutes", "walletStatus", "offlineAllowancePiastres"],
            Names(CompanyOf(body, company)));
        Assert.Equal("INSUFFICIENT", CompanyOf(body, company).GetProperty("walletStatus").GetString());
        Assert.Equal(0, CompanyOf(body, company).GetProperty("offlineAllowancePiastres").GetInt64());
        // The tag key is this device's HMAC under the lookup key, base64 with padding as Java's Base64 encoder writes it.
        Assert.Equal(44, body.GetProperty("tagKey").GetString()!.Length);
    }

    [Fact]
    public async Task ATagWhoseUidDoesNotMatchItsLookupHashNeverReachesADevice()
    {
        // ADR 0027: a tag whose decrypted UID does not hash to its uid_lookup (a UID swapped in under a data key looks so) is
        // left out; every other tag still goes out.
        var pump = await F.PumpAsync();
        var company = await F.CompanyAsync();
        var kept = await F.CarAsync(company);
        // A tag written past the application, its lookup hash another UID's: a swapped ciphertext looks so.
        var vehicle = await BodyAsync(await F.Id.AuthedAsync("POST", "/api/v1/ops/vehicles", await F.OpsAsync(),
            $$$"""{"companyId":"{{{company}}}","vehicle":{"plate":"FT SWAP{{{Random.Shared.Next(100_000)}}}","make":"Kia","model":"Rio","fuelType":"GAS_92","tankCapacityMl":40000,"tier":"NORMAL"}}"""),
            201);
        var swappedVehicle = vehicle.GetProperty("id").GetString()!;
        var swappedUid = RandomUid();
        var id = Guid.CreateVersion7();
        await o.Owner.ExecuteAsync("""
            insert into rfid_tags (id, company_id, vehicle_id, uid_encrypted, uid_lookup, uid_last4, status, created_at, updated_at)
            values (@id, @company::uuid, @vehicle::uuid, @encrypted, @lookup, 'XXXX', 'ACTIVE', now(), now())
            """, new
        {
            id, company, vehicle = swappedVehicle,
            encrypted = api.Factory.Services.GetRequiredService<DataEncryptor>().Encrypt(Encoding.ASCII.GetBytes(swappedUid), Tags.AssociatedData(id)),
            lookup = api.Factory.Services.GetRequiredService<LookupHasher>().Hash(Tags.LookupNamespace, RandomUid()),
        });

        var body = await BodyAsync(await SnapshotAsync(pump.DeviceToken), 200);
        var key = body.GetProperty("tagKey").GetString()!;
        var digests = body.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("digest").GetString()).ToList();
        Assert.Contains(Digest(key, kept.TagUid), digests);
        Assert.DoesNotContain(Digest(key, swappedUid), digests);
        Assert.DoesNotContain(body.GetProperty("vehicles").EnumerateArray(), v => v.GetProperty("id").GetString() == swappedVehicle);
    }

    [Fact]
    public async Task AShortUidIsLeftOutBecauseEvenATruncatedDigestWouldGiveItAway()
    {
        var pump = await F.PumpAsync();
        var company = await F.CompanyAsync();
        var longUid = await F.CarAsync(company);
        var vehicle = await BodyAsync(await F.Id.AuthedAsync("POST", "/api/v1/ops/vehicles", await F.OpsAsync(),
            $$$"""{"companyId":"{{{company}}}","vehicle":{"plate":"FT SHORT{{{Random.Shared.Next(100_000)}}}","make":"Kia","model":"Rio","fuelType":"GAS_92","tankCapacityMl":40000,"tier":"NORMAL"}}"""),
            201);
        var shortUid = Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
        Assert.Equal(201, (int)(await F.Id.AuthedAsync("POST", $"/api/v1/ops/vehicles/{vehicle.GetProperty("id").GetString()}/tags",
            await F.OpsAsync(), $$"""{"uid":"{{shortUid}}"}""")).StatusCode);

        var body = await BodyAsync(await SnapshotAsync(pump.DeviceToken), 200);
        var key = body.GetProperty("tagKey").GetString()!;
        var digests = body.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("digest").GetString()!).ToList();
        Assert.All(digests, d => Assert.Equal(8, d.Length));
        Assert.Contains(Digest(key, longUid.TagUid), digests);
        Assert.DoesNotContain(Digest(key, shortUid), digests);
    }

    [Fact]
    public async Task WhileTheVipFlowIsOffNoVehicleIsMarkedVip()
    {
        var pump = await F.PumpAsync();
        var company = await F.CompanyAsync();
        var vip = await F.CarAsync(company, "GAS_92", "VIP", 30_000L, 60_000);
        var body = await BodyAsync(await SnapshotAsync(pump.DeviceToken), 200);
        Assert.Equal([false], body.GetProperty("vehicles").EnumerateArray().Where(v => v.GetProperty("id").GetString() == vip.VehicleId)
            .Select(v => v.GetProperty("vip").GetBoolean()));
    }

    [Fact]
    public async Task TheAllowanceIsCappedSoTheBalanceStaysPrivate()
    {
        var pump = await F.PumpAsync();
        var company = await F.CompanyAsync();
        await F.CreditAsync(company, 9_000_000);
        await F.CarAsync(company);
        var response = await SnapshotAsync(pump.DeviceToken);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(500_000, CompanyOf(await BodyAsync(response, 200), company).GetProperty("offlineAllowancePiastres").GetInt64());
        Assert.DoesNotContain("9000000", text, StringComparison.Ordinal);

        var small = await F.CompanyAsync();
        await F.CreditAsync(small, 123_456);
        await F.CarAsync(small);
        // A balance under the cap, rounded down.
        Assert.Equal(120_000, CompanyOf(await BodyAsync(await SnapshotAsync(pump.DeviceToken), 200), small)
            .GetProperty("offlineAllowancePiastres").GetInt64());
    }

    [Fact]
    public async Task EachDeviceGetsItsOwnTagKey()
    {
        var first = await F.PumpAsync();
        var second = await F.PumpAsync();
        var car = await F.CarAsync(await F.CompanyAsync());
        var a = (await BodyAsync(await SnapshotAsync(first.DeviceToken), 200)).GetProperty("tagKey").GetString()!;
        var b = (await BodyAsync(await SnapshotAsync(second.DeviceToken), 200)).GetProperty("tagKey").GetString()!;
        Assert.NotEqual(a, b);
        Assert.NotEqual(Digest(a, car.TagUid), Digest(b, car.TagUid));
    }

    [Fact]
    public async Task OnlyADeviceGetsASnapshot()
    {
        var user = await F.Pos.PosAsync("GET", "/api/v1/pos/snapshot", await F.OpsAsync(), null, null);
        Assert.Equal(401, (int)user.StatusCode);
        Assert.Equal("DEVICE_UNAUTHENTICATED", await CodeAsync(user));
        Assert.Equal(401, (int)(await F.Id.GetAsync("/api/v1/pos/snapshot")).StatusCode);
    }

    [Fact]
    public async Task AnOutdatedTerminalIsToldToUpdate()
    {
        var pump = await F.PumpAsync();
        var old = await F.Pos.PosAsync("GET", "/api/v1/pos/snapshot", pump.DeviceToken, null, null, contract: "0");
        Assert.Equal(426, (int)old.StatusCode);
        var refusal = await BodyAsync(old, 426);
        Assert.Equal("CONTRACT_UPGRADE_REQUIRED", refusal.GetProperty("code").GetString());
        Assert.Equal(1, refusal.GetProperty("params").GetProperty("minimum").GetInt32());
        Assert.Equal(200, (int)(await F.Pos.PosAsync("GET", "/api/v1/pos/snapshot", pump.DeviceToken, null, null, contract: "1")).StatusCode);
        Assert.Equal(1, (await BodyAsync(await F.Id.GetAsync("/api/v1/meta"), 200)).GetProperty("posContract").GetInt32());
    }

    [Fact]
    public async Task TheSyncMonitorShowsEachAudienceItsDevices()
    {
        var pump = await F.PumpAsync();
        await SnapshotAsync(pump.DeviceToken);
        var all = await BodyAsync(await F.GetAsync($"/api/v1/ops/sync/devices?operatorId={pump.OperatorId}", await F.OpsAsync()), 200);
        Assert.Equal([pump.DeviceId], all.GetProperty("items").EnumerateArray().Select(d => d.GetProperty("deviceId").GetString()));
        Assert.False(all.GetProperty("items")[0].GetProperty("stale").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, all.GetProperty("items")[0].GetProperty("lastSyncAt").ValueKind);

        var station = await F.SignInAsync(Role.STATION_MANAGER, Scope.Operator(Guid.Parse(pump.OperatorId)));
        Assert.Equal([pump.DeviceId], (await BodyAsync(await F.GetAsync("/api/v1/station/sync/devices", station), 200))
            .GetProperty("items").EnumerateArray().Select(d => d.GetProperty("deviceId").GetString()));
        var other = (await F.Id.SignInAsync(await F.Id.SeedAsync(Role.STATION_MANAGER))).AccessToken;
        Assert.Empty((await BodyAsync(await F.GetAsync("/api/v1/station/sync/devices", other), 200)).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task TheMonitorShowsTheLastBatchAndEveryBatchNewestFirst()
    {
        // B5 contract §5, beyond Java's test: the last batch's counts and pending, the batch list by device, stale filtering,
        // and the station manager seeing only the operator's own batches through row-level security.
        var pump = await F.PumpAsync();
        await F.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await F.CompanyAsync();
        await F.CreditAsync(company, 100_000);
        var good = await o.CaptureAsync(pump, await F.CarAsync(company), 1_000);
        var unknown = new Capture(Guid.NewGuid(), RandomUid(), 1_000, null, DateTimeOffset.UtcNow, await o.AgentIdAsync(pump));
        var firstBatch = Guid.NewGuid();
        var secondBatch = Guid.NewGuid();
        await OfflineTestSupport.AnsweredAsync(await o.SyncAsync(pump, firstBatch, 4, [good, unknown]));
        await OfflineTestSupport.AnsweredAsync(await o.SyncAsync(pump, secondBatch, 2, [good]));
        var ops = await F.OpsAsync();

        var device = (await BodyAsync(await F.GetAsync($"/api/v1/ops/sync/devices?stationId={pump.StationId}", ops), 200)).GetProperty("items")[0];
        var last = device.GetProperty("lastBatch");
        Assert.Equal(["receivedAt", "items", "recorded", "duplicates", "rejected", "withConflicts", "pending"],
            last.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, last.GetProperty("items").GetInt32());
        Assert.Equal(1, last.GetProperty("duplicates").GetInt32());
        Assert.Equal(2, last.GetProperty("pending").GetInt32());
        Assert.Equal(pump.DeviceCode, device.GetProperty("code").GetString());
        Assert.Equal("Test Station", device.GetProperty("stationNameEn").GetString());

        var batches = (await BodyAsync(await F.GetAsync($"/api/v1/ops/sync/batches?deviceId={pump.DeviceId}", ops), 200))
            .GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([secondBatch.ToString(), firstBatch.ToString()], batches.Select(b => b.GetProperty("deviceBatchId").GetString()));
        Assert.Equal(1, batches[1].GetProperty("recorded").GetInt32());
        Assert.Equal(1, batches[1].GetProperty("rejected").GetInt32());
        Assert.Equal(4, batches[1].GetProperty("pending").GetInt32());
        var paged = await BodyAsync(await F.GetAsync($"/api/v1/ops/sync/batches?deviceId={pump.DeviceId}&limit=1", ops), 200);
        Assert.Equal(secondBatch.ToString(), paged.GetProperty("items")[0].GetProperty("deviceBatchId").GetString());
        var next = await BodyAsync(await F.GetAsync(
            $"/api/v1/ops/sync/batches?deviceId={pump.DeviceId}&limit=1&cursor={paged.GetProperty("nextCursor").GetString()}", ops), 200);
        Assert.Equal(firstBatch.ToString(), next.GetProperty("items")[0].GetProperty("deviceBatchId").GetString());

        // Not seen for more than a day: stale, and found by the stale filter.
        await o.Owner.ExecuteAsync("update pos_devices set last_sync_at = now() - interval '25 hours' where id = @d::uuid", new { d = pump.DeviceId });
        var stale = await BodyAsync(await F.GetAsync($"/api/v1/ops/sync/devices?stationId={pump.StationId}&stale=true", ops), 200);
        Assert.True(stale.GetProperty("items")[0].GetProperty("stale").GetBoolean());

        // RLS under the monitor: another operator's manager sees none of these batches.
        var otherOperator = (await F.Id.SignInAsync(await F.Id.SeedAsync(Role.STATION_MANAGER))).AccessToken;
        var mine = await o.Owner.AsRuntimeRoleAsync("OPERATOR", Guid.Parse(pump.OperatorId), null, async (c, t) =>
            await Dapper.SqlMapper.ExecuteScalarAsync<long>(c, "select count(*) from sync_batches", transaction: t));
        Assert.Equal(2, mine);
        var notMine = await o.Owner.AsRuntimeRoleAsync("OPERATOR", Guid.CreateVersion7(), null, async (c, t) =>
            await Dapper.SqlMapper.ExecuteScalarAsync<long>(c, "select count(*) from sync_batches where device_id = @d::uuid",
                new { d = pump.DeviceId }, t));
        Assert.Equal(0, notMine);
        Assert.Empty((await BodyAsync(await F.GetAsync("/api/v1/station/sync/devices", otherOperator), 200)).GetProperty("items").EnumerateArray());
    }

    // ----------------------------------------------------------------------------------------------------- helpers

    private Task<HttpResponseMessage> SnapshotAsync(string deviceToken) => F.Pos.PosAsync("GET", "/api/v1/pos/snapshot", deviceToken, null, null);

    private static JsonElement CompanyOf(JsonElement body, string company) =>
        body.GetProperty("companies").EnumerateArray().Single(c => c.GetProperty("id").GetString() == company);

    /// <summary>What the device computes: the first 8 hex characters of HMAC-SHA256(tagKey, normalised UID).</summary>
    public static string Digest(string tagKeyBase64, string uid) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Convert.FromBase64String(tagKeyBase64), Encoding.ASCII.GetBytes(uid.ToUpperInvariant())))[..8];
}
