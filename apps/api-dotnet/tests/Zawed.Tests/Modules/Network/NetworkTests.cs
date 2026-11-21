using System.Text.Json.Nodes;
using Zawed.Api.Modules.Network;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Network.PosTestSupport;

namespace Zawed.Tests.Modules.Network;

/// <summary>
/// Operators, stations, POS devices and agents: one-time secrets and the station portal's view (Java <c>NetworkTests</c>),
/// then the B2 rules the Java class did not pin: coordinates as exact numbers, validation codes, 404s, audit rows, shifts.
/// </summary>
public sealed class NetworkTests(SharedApi api) : IClassFixture<SharedApi>
{
    private readonly PosTestSupport pos = new(api.Factory);

    private async Task<HttpResponseMessage> OpsAsync(string method, string path, string? json = null) =>
        await pos.Id.AuthedAsync(method, path, await pos.Ops.Value, json);

    [Fact]
    public async Task ADeviceActivationCodeIsShownOnceAndStoredOnlyAsAKeyedHash()
    {
        var station = await StationAsync(await pos.OperatorAsync());
        var registered = await OpsAsync("POST", "/api/v1/ops/devices", $$"""{"stationId":"{{station}}","label":"Pump 1"}""");
        var body = await Json(registered, 201);
        Assert.Contains("no-store", registered.Headers.CacheControl!.ToString(), StringComparison.Ordinal);

        var code = body["activationCode"]!.GetValue<string>();
        var device = body["device"]!["id"]!.GetValue<string>();
        Assert.Matches("^[A-Z2-9]{12}$", code);
        Assert.Equal("PENDING_ACTIVATION", body["device"]!["status"]!.GetValue<string>());
        Assert.Equal("Pump 1", body["device"]!["label"]!.GetValue<string>());
        Assert.Matches("^POS-[A-Z2-9]{8}$", body["device"]!["code"]!.GetValue<string>());
        var stored = await pos.Owner.QueryOneAsync<string>("select activation_code_hash from pos_devices where id = @id::uuid", new { id = device });
        Assert.NotEqual(code, stored);
        Assert.Equal(64, stored!.Length);

        // Reading the device later never returns the code.
        var read = await (await OpsAsync("GET", $"/api/v1/ops/devices/{device}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(code, read, StringComparison.Ordinal);
        Assert.DoesNotContain("activationCode", read, StringComparison.Ordinal);

        // A new code replaces the old one; after revocation there is no code at all.
        var reissued = await OpsAsync("POST", $"/api/v1/ops/devices/{device}/activation-code");
        var again = (await Json(reissued, 200))["activationCode"]!.GetValue<string>();
        Assert.Contains("no-store", reissued.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        Assert.NotEqual(code, again);
        var revoked = await Json(await OpsAsync("POST", $"/api/v1/ops/devices/{device}/revoke", """{"reason":"  Stolen  "}"""), 200);
        Assert.Equal("REVOKED", revoked["status"]!.GetValue<string>());
        Assert.Equal("Stolen", revoked["revokedReason"]!.GetValue<string>());
        Assert.Null(await pos.Owner.QueryOneAsync<string>("select activation_code_hash from pos_devices where id = @id::uuid", new { id = device }));
        await Http.AssertErrorAsync(await OpsAsync("POST", $"/api/v1/ops/devices/{device}/activation-code"), 409, "DEVICE_NOT_PENDING");
        // Revoking again changes nothing.
        Assert.Equal(1L, await pos.Owner.QueryOneAsync<long>("select count(*) from audit_log where entity_id = @id::uuid and action = 'DEVICE_REVOKED'",
            new { id = device }));
        await Json(await OpsAsync("POST", $"/api/v1/ops/devices/{device}/revoke", """{"reason":"Again"}"""), 200);
        Assert.Equal(1L, await pos.Owner.QueryOneAsync<long>("select count(*) from audit_log where entity_id = @id::uuid and action = 'DEVICE_REVOKED'",
            new { id = device }));
    }

    [Fact]
    public async Task AnAgentPinIsGeneratedShownOnceAndHashedWithArgon2()
    {
        var station = await StationAsync(await pos.OperatorAsync());
        var created = await OpsAsync("POST", "/api/v1/ops/agents", $$"""{"stationId":"{{station}}","fullName":"Mahmoud Adel","phone":"01000000000"}""");
        var body = await Json(created, 201);
        Assert.Contains("no-store", created.Headers.CacheControl!.ToString(), StringComparison.Ordinal);

        var pin = body["pin"]!.GetValue<string>();
        var agent = body["agent"]!["id"]!.GetValue<string>();
        Assert.Matches("^[0-9]{6}$", pin);
        Assert.False(Agents.IsTrivial(pin));
        Assert.Matches("^W[0-9]{7}$", body["agent"]!["workerId"]!.GetValue<string>());
        Assert.True(body["agent"]!["mustChangePin"]!.GetValue<bool>());
        Assert.StartsWith("$argon2id$", await pos.Owner.QueryOneAsync<string>("select pin_hash from agents where id = @id::uuid", new { id = agent }),
            StringComparison.Ordinal);

        var reset = await OpsAsync("POST", $"/api/v1/ops/agents/{agent}/reset-pin");
        Assert.Matches("^[0-9]{6}$", (await Json(reset, 200))["pin"]!.GetValue<string>());
        Assert.Contains("no-store", reset.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("pin\"", await (await OpsAsync("GET", $"/api/v1/ops/agents/{agent}")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public void TrivialPinsAreRecognised()
    {
        Assert.True(Agents.IsTrivial("000000"));
        Assert.True(Agents.IsTrivial("123456"));
        Assert.True(Agents.IsTrivial("987654"));
        Assert.False(Agents.IsTrivial("120456"));
        for (var i = 0; i < 200; i++)
        {
            Assert.False(Agents.IsTrivial(Agents.NewPin()));
        }
    }

    [Fact]
    public async Task AStationManagerSeesOnlyTheirOperator()
    {
        var mine = await pos.OperatorAsync();
        var theirs = await pos.OperatorAsync();
        var myStation = await StationAsync(mine);
        var theirStation = await StationAsync(theirs);
        var myDevice = (await pos.RegisterDeviceAsync(myStation)).Id;
        await pos.RegisterDeviceAsync(theirStation);
        await pos.AgentAsync(theirStation);

        var manager = (await pos.Id.SignInAsync(await pos.Id.SeedAsync(Role.STATION_MANAGER, Scope.Operator(Guid.Parse(mine)), false))).AccessToken;
        var stations = await Json(await pos.Id.AuthedAsync("GET", "/api/v1/station/stations", manager), 200);
        Assert.Equal([myStation], stations["items"]!.AsArray().Select(s => s!["id"]!.GetValue<string>()));
        await Http.AssertErrorAsync(await pos.Id.AuthedAsync("GET", $"/api/v1/station/stations/{theirStation}", manager), 404, "NOT_FOUND");
        Assert.Equal(mine, (await Json(await pos.Id.AuthedAsync("GET", "/api/v1/station/operator", manager), 200))["id"]!.GetValue<string>());
        var devices = await Json(await pos.Id.AuthedAsync("GET", "/api/v1/station/devices", manager), 200);
        Assert.Equal([myDevice], devices["items"]!.AsArray().Select(d => d!["id"]!.GetValue<string>()));
        Assert.Empty((await Json(await pos.Id.AuthedAsync("GET", "/api/v1/station/agents", manager), 200))["items"]!.AsArray());
        // Even a filter naming the other operator's station finds nothing.
        Assert.Empty((await Json(await pos.Id.AuthedAsync("GET", $"/api/v1/station/devices?stationId={theirStation}", manager), 200))["items"]!.AsArray());
    }

    [Fact]
    public async Task AClosedStationIsFinal()
    {
        var station = await StationAsync(await pos.OperatorAsync());
        await Json(await OpsAsync("POST", $"/api/v1/ops/stations/{station}/status", """{"status":"CLOSED","reason":"Demolished"}"""), 200);
        await Http.AssertErrorAsync(await OpsAsync("POST", $"/api/v1/ops/stations/{station}/status", """{"status":"ACTIVE","reason":"Oops"}"""),
            409, "STATION_CLOSED");
        await Http.AssertErrorAsync(await OpsAsync("POST", "/api/v1/ops/devices", $$"""{"stationId":"{{station}}"}"""), 409, "STATION_CLOSED");
        await Http.AssertErrorAsync(await OpsAsync("POST", "/api/v1/ops/agents", $$"""{"stationId":"{{station}}","fullName":"Late"}"""), 409, "STATION_CLOSED");
    }

    [Fact]
    public async Task AnUnknownTimeZoneIsRefused()
    {
        var @operator = await pos.OperatorAsync();
        foreach (var zone in new[] { "Mars/Base", "africa/cairo", "Egypt Standard Time", "+19:00", "UTC+x" })
        {
            var response = await OpsAsync("POST", "/api/v1/ops/stations",
                $$"""{"operatorId":"{{@operator}}","station":{"nameAr":"م","nameEn":"S","timeZone":"{{zone}}","connectivity":"LOW","fuelTypes":["DIESEL"]} }""");
            var refused = await Http.AssertErrorAsync(response, 400, "INVALID_TIME_ZONE");
            Assert.Equal("timeZone", refused.GetProperty("field").GetString());
        }
    }

    [Theory]
    [InlineData("Europe/London", "Europe/London")]
    [InlineData("UTC", "UTC")]
    [InlineData("+2", "+02:00")]
    [InlineData("Z", "Z")]
    [InlineData("UTC+2", "UTC+02:00")]
    [InlineData("GMT+0", "GMT")]
    [InlineData("-0530", "-05:30")]
    public void AZoneIsNormalisedAsJavaNormalisesIt(string typed, string stored) => Assert.Equal(stored, Zones.Normalise(typed));

    [Fact]
    public async Task AnAgentPhoneIsAnEgyptianMobileStoredAs01XXXXXXXXX()
    {
        var station = await StationAsync(await pos.OperatorAsync());
        var created = await OpsAsync("POST", "/api/v1/ops/agents", $$"""{"stationId":"{{station}}","fullName":"Phone Agent","phone":"+20 112 345 6789"}""");
        Assert.Equal("01123456789", (await Json(created, 201))["agent"]!["phone"]!.GetValue<string>());

        foreach (var phone in new[] { "0223456789", "12345", "01301234567", "agent" })
        {
            var refused = await OpsAsync("POST", "/api/v1/ops/agents", $$"""{"stationId":"{{station}}","fullName":"Phone Agent","phone":"{{phone}}"}""");
            var body = await Http.AssertErrorAsync(refused, 400, "VALIDATION_FAILED");
            Assert.Equal(["phone"], body.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("field").GetString()));
            Assert.Equal("PATTERN", body.GetProperty("fields")[0].GetProperty("code").GetString());
        }
        var blank = await OpsAsync("POST", "/api/v1/ops/agents", $$"""{"stationId":"{{station}}","fullName":"No Phone","phone":" "}""");
        Assert.Null((await Json(blank, 201))["agent"]!["phone"]);
    }

    /// <summary>
    /// QA round 1: a device whose activation code had expired showed "awaiting activation", and a locked agent showed
    /// "active". Ops and the station manager now read both states from the API.
    /// </summary>
    [Fact]
    public async Task OpsAndTheStationManagerSeeAnExpiredActivationCodeAndALockedAgent()
    {
        var @operator = await pos.OperatorAsync();
        var station = await StationAsync(@operator);
        var device = (await pos.RegisterDeviceAsync(station)).Id;
        var agentCreated = await Json(await OpsAsync("POST", "/api/v1/ops/agents", $$"""{"stationId":"{{station}}","fullName":"Locked Agent"}"""), 201);
        var agent = agentCreated["agent"]!["id"]!.GetValue<string>();
        Assert.False(agentCreated["agent"]!["locked"]!.GetValue<bool>());
        Assert.True(agentCreated["agent"]!["mustChangePin"]!.GetValue<bool>());

        Assert.False((await Json(await OpsAsync("GET", $"/api/v1/ops/devices/{device}"), 200))["activationExpired"]!.GetValue<bool>());

        // Fifteen minutes later the code no longer works, and an agent typed a wrong PIN too often.
        await pos.Owner.ExecuteAsync("update pos_devices set activation_expires_at = now() - interval '1 minute' where id = @id::uuid", new { id = device });
        await pos.Owner.ExecuteAsync("update agents set locked_until = now() + interval '10 minutes', failed_pin_count = 5 where id = @id::uuid", new { id = agent });

        var expired = await Json(await OpsAsync("GET", $"/api/v1/ops/devices/{device}"), 200);
        Assert.Equal("PENDING_ACTIVATION", expired["status"]!.GetValue<string>());
        Assert.True(expired["activationExpired"]!.GetValue<bool>());
        var locked = await Json(await OpsAsync("GET", $"/api/v1/ops/agents/{agent}"), 200);
        Assert.True(locked["locked"]!.GetValue<bool>());
        Assert.NotNull(locked["lockedUntil"]);

        var manager = (await pos.Id.SignInAsync(await pos.Id.SeedAsync(Role.STATION_MANAGER, Scope.Operator(Guid.Parse(@operator)), false))).AccessToken;
        var devices = await Json(await pos.Id.AuthedAsync("GET", "/api/v1/station/devices", manager), 200);
        Assert.True(devices["items"]!.AsArray().Single(d => d!["id"]!.GetValue<string>() == device)!["activationExpired"]!.GetValue<bool>());
        var agents = await Json(await pos.Id.AuthedAsync("GET", "/api/v1/station/agents", manager), 200);
        var mine = agents["items"]!.AsArray().Single(a => a!["id"]!.GetValue<string>() == agent)!;
        Assert.True(mine["locked"]!.GetValue<bool>());
        Assert.True(mine["mustChangePin"]!.GetValue<bool>());

        // Ops reissues a code, and the device is no longer expired. A lock in the past is no lock.
        await OpsAsync("POST", $"/api/v1/ops/devices/{device}/activation-code");
        Assert.False((await Json(await OpsAsync("GET", $"/api/v1/ops/devices/{device}"), 200))["activationExpired"]!.GetValue<bool>());
        await pos.Owner.ExecuteAsync("update agents set locked_until = now() - interval '1 minute' where id = @id::uuid", new { id = agent });
        Assert.False((await Json(await OpsAsync("GET", $"/api/v1/ops/agents/{agent}"), 200))["locked"]!.GetValue<bool>());
    }

    // ------------------------------------------------------------------------------- the B2 rules beyond Java's class

    [Fact]
    public async Task CoordinatesAreExactNumbersStoredWithSixDecimals()
    {
        var @operator = await pos.OperatorAsync();
        var created = await OpsAsync("POST", "/api/v1/ops/stations",
            $$"""{"operatorId":"{{@operator}}","station":{"nameAr":"م","nameEn":"S","latitude":30.0444,"longitude":-31.2357,"connectivity":"NORMAL","fuelTypes":["DIESEL","GAS_92","DIESEL"]} }""");
        var text = await created.Content.ReadAsStringAsync();
        Assert.Equal(201, (int)created.StatusCode);
        // What Jackson writes for the BigDecimal JDBC reads from numeric(9, 6).
        Assert.Contains("\"latitude\":30.044400,\"longitude\":-31.235700", text, StringComparison.Ordinal);
        // A set, in the enum's order.
        Assert.Contains("\"fuelTypes\":[\"GAS_92\",\"DIESEL\"]", text, StringComparison.Ordinal);
        Assert.Contains("\"timeZone\":\"Africa/Cairo\"", text, StringComparison.Ordinal);
        var id = JsonNode.Parse(text)!["id"]!.GetValue<string>();
        Assert.Equal("{GAS_92,DIESEL}", await pos.Owner.QueryOneAsync<string>("select fuel_types::text from stations where id = @id::uuid", new { id }));

        // A string is coerced as Jackson coerces it; zero keeps its scale.
        var updated = await OpsAsync("PUT", $"/api/v1/ops/stations/{id}",
            """{"nameAr":"م","nameEn":"S","latitude":"0","longitude":1E+1,"connectivity":"LOW","fuelTypes":["CNG"]}""");
        Assert.Contains("\"latitude\":0.000000,\"longitude\":10.000000", await updated.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var audit = await pos.Owner.QueryOneAsync<string>("select metadata::text from audit_log where entity_id = @id::uuid and action = 'STATION_UPDATED'", new { id });
        Assert.Contains("\"connectivity\": {\"to\": \"LOW\", \"from\": \"NORMAL\"}", audit, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"latitude\":90.5,\"longitude\":0", "station.latitude", "DECIMAL_MAX")]
    [InlineData("\"latitude\":-91,\"longitude\":0", "station.latitude", "DECIMAL_MIN")]
    [InlineData("\"latitude\":1.1234567,\"longitude\":0", "station.latitude", "DIGITS")]
    [InlineData("\"latitude\":0,\"longitude\":180.000001", "station.longitude", "DECIMAL_MAX")]
    [InlineData("\"latitude\":0,\"longitude\":1000", "station.longitude", "DECIMAL_MAX")]
    public async Task CoordinatesOutOfRangeAreRefusedByField(string coordinates, string field, string code)
    {
        var @operator = await pos.OperatorAsync();
        var response = await OpsAsync("POST", "/api/v1/ops/stations",
            $$"""{"operatorId":"{{@operator}}","station":{"nameAr":"م","nameEn":"S",{{coordinates}},"connectivity":"NORMAL","fuelTypes":["DIESEL"]} }""");
        var body = await Http.AssertErrorAsync(response, 400, "VALIDATION_FAILED");
        Assert.Contains(body.GetProperty("fields").EnumerateArray(), f => f.GetProperty("field").GetString() == field && f.GetProperty("code").GetString() == code);
    }

    [Fact]
    public async Task AStationNeedsAKnownOperatorAndBothCoordinatesOrNeither()
    {
        var unknown = await Http.AssertErrorAsync(await OpsAsync("POST", "/api/v1/ops/stations",
            $$"""{"operatorId":"{{Guid.CreateVersion7()}}","station":{"nameAr":"م","nameEn":"S","connectivity":"NORMAL","fuelTypes":["DIESEL"]} }"""),
            400, "OPERATOR_NOT_FOUND");
        Assert.Equal("operatorId", unknown.GetProperty("field").GetString());
        var half = await Http.AssertErrorAsync(await OpsAsync("POST", "/api/v1/ops/stations",
            $$"""{"operatorId":"{{await pos.OperatorAsync()}}","station":{"nameAr":"م","nameEn":"S","latitude":30,"connectivity":"NORMAL","fuelTypes":["DIESEL"]} }"""),
            400, "INCOMPLETE_LOCATION");
        Assert.Equal("latitude", half.GetProperty("field").GetString());
        var empty = await Http.AssertErrorAsync(await OpsAsync("POST", "/api/v1/ops/stations",
            $$"""{"operatorId":"{{await pos.OperatorAsync()}}","station":{"nameAr":" ","nameEn":"S","connectivity":"NORMAL","fuelTypes":[]} }"""),
            400, "VALIDATION_FAILED");
        Assert.Equal(["station.fuelTypes", "station.nameAr"],
            empty.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("field").GetString()).Order());
    }

    [Fact]
    public async Task UnknownRowsAnswer404()
    {
        var id = Guid.CreateVersion7();
        foreach (var (method, path, body) in new (string, string, string?)[]
        {
            ("GET", $"/api/v1/ops/operators/{id}", null), ("PUT", $"/api/v1/ops/operators/{id}", """{"legalName":"L","nameAr":"م","nameEn":"N"}"""),
            ("POST", $"/api/v1/ops/operators/{id}/status", """{"status":"SUSPENDED","reason":"x"}"""),
            ("GET", $"/api/v1/ops/stations/{id}", null),
            ("PUT", $"/api/v1/ops/stations/{id}", """{"nameAr":"م","nameEn":"S","connectivity":"LOW","fuelTypes":["CNG"]}"""),
            ("POST", $"/api/v1/ops/stations/{id}/status", """{"status":"SUSPENDED","reason":"x"}"""),
            ("GET", $"/api/v1/ops/devices/{id}", null), ("POST", $"/api/v1/ops/devices/{id}/activation-code", null),
            ("POST", $"/api/v1/ops/devices/{id}/revoke", """{"reason":"x"}"""), ("POST", "/api/v1/ops/devices", $$"""{"stationId":"{{id}}"}"""),
            ("GET", $"/api/v1/ops/agents/{id}", null), ("POST", $"/api/v1/ops/agents/{id}/reset-pin", null),
            ("POST", $"/api/v1/ops/agents/{id}/status", """{"status":"SUSPENDED","reason":"x"}"""),
            ("POST", "/api/v1/ops/agents", $$"""{"stationId":"{{id}}","fullName":"A"}"""),
        })
        {
            var response = await OpsAsync(method, path, body);
            Assert.True((int)response.StatusCode == 404, $"{method} {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }
    }

    [Fact]
    public async Task EveryChangeIsAuditedUnderTheOperatorsScope()
    {
        var @operator = await pos.OperatorAsync();
        var station = await StationAsync(@operator);
        var device = (await pos.RegisterDeviceAsync(station)).Id;
        var agent = (await Json(await OpsAsync("POST", "/api/v1/ops/agents", $$"""{"stationId":"{{station}}","fullName":"A"}"""), 201))["agent"]!["id"]!.GetValue<string>();
        await Json(await OpsAsync("PUT", $"/api/v1/ops/operators/{@operator}", """{"legalName":"Renamed","nameAr":"م","nameEn":"N"}"""), 200);
        var suspended = await Json(await OpsAsync("POST", $"/api/v1/ops/operators/{@operator}/status", """{"status":"SUSPENDED","reason":" Late "}"""), 200);
        Assert.Equal("SUSPENDED", suspended["status"]!.GetValue<string>());
        await Json(await OpsAsync("POST", $"/api/v1/ops/stations/{station}/status", """{"status":"SUSPENDED","reason":"Works"}"""), 200);
        await Json(await OpsAsync("POST", $"/api/v1/ops/agents/{agent}/status", """{"status":"DEACTIVATED","reason":"Left"}"""), 200);
        await Http.AssertErrorAsync(await OpsAsync("POST", $"/api/v1/ops/agents/{agent}/status", """{"status":"ACTIVE","reason":"Back"}"""), 409, "AGENT_DEACTIVATED");
        await Http.AssertErrorAsync(await OpsAsync("POST", $"/api/v1/ops/agents/{agent}/reset-pin"), 409, "AGENT_DEACTIVATED");

        var rows = await pos.Owner.QueryAsync<(string Action, string ScopeType, Guid ScopeId)>(
            "select action, scope_type, scope_id from audit_log where entity_id in (@o::uuid, @s::uuid, @d::uuid, @a::uuid) order by occurred_at",
            new { o = @operator, s = station, d = device, a = agent });
        Assert.Equal(["OPERATOR_CREATED", "STATION_CREATED", "DEVICE_REGISTERED", "AGENT_CREATED", "OPERATOR_UPDATED", "OPERATOR_STATUS_CHANGED",
            "STATION_STATUS_CHANGED", "AGENT_STATUS_CHANGED"], rows.Select(r => r.Action));
        Assert.All(rows, r => Assert.Equal(("OPERATOR", Guid.Parse(@operator)), (r.ScopeType, r.ScopeId)));
        Assert.Contains("\"reason\": \"Late\"", await pos.Owner.QueryOneAsync<string>(
            "select metadata::text from audit_log where entity_id = @o::uuid and action = 'OPERATOR_STATUS_CHANGED'", new { o = @operator }), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListsFilterSearchAndPage()
    {
        var @operator = await pos.OperatorAsync();
        var station = await StationAsync(@operator);
        var agents = new List<(string Id, string Worker)>();
        for (var i = 0; i < 3; i++)
        {
            var created = await Json(await OpsAsync("POST", "/api/v1/ops/agents", $$"""{"stationId":"{{station}}","fullName":"Searchable_{{i}} Agent"}"""), 201);
            agents.Add((created["agent"]!["id"]!.GetValue<string>(), created["agent"]!["workerId"]!.GetValue<string>()));
        }
        var first = await Json(await OpsAsync("GET", $"/api/v1/ops/agents?stationId={station}&limit=2"), 200);
        Assert.Equal([agents[2].Id, agents[1].Id], first["items"]!.AsArray().Select(a => a!["id"]!.GetValue<string>()));
        var next = await Json(await OpsAsync("GET", $"/api/v1/ops/agents?stationId={station}&limit=2&cursor={first["nextCursor"]}"), 200);
        Assert.Equal([agents[0].Id], next["items"]!.AsArray().Select(a => a!["id"]!.GetValue<string>()));
        Assert.Null(next["nextCursor"]);
        // q matches the name (LIKE, _ literal) or the worker id as typed in lower case.
        var byWorker = await Json(await OpsAsync("GET", $"/api/v1/ops/agents?q={agents[1].Worker.ToLowerInvariant()}"), 200);
        Assert.Equal([agents[1].Id], byWorker["items"]!.AsArray().Select(a => a!["id"]!.GetValue<string>()));
        var byName = await Json(await OpsAsync("GET", $"/api/v1/ops/agents?stationId={station}&q=searchable_1"), 200);
        Assert.Equal([agents[1].Id], byName["items"]!.AsArray().Select(a => a!["id"]!.GetValue<string>()));
        await Http.AssertErrorAsync(await OpsAsync("GET", "/api/v1/ops/agents?cursor=nope"), 400, "INVALID_CURSOR");
        Assert.Single((await Json(await OpsAsync("GET", $"/api/v1/ops/stations?operatorId={@operator}"), 200))["items"]!.AsArray());
        Assert.Empty((await Json(await OpsAsync("GET", $"/api/v1/ops/stations?operatorId={@operator}&status=CLOSED"), 200))["items"]!.AsArray());
    }

    [Fact]
    public async Task ShiftsShowWhoWorkedOnWhichDeviceAndHowTheyEnded()
    {
        var pump = await pos.PumpAsync();
        Assert.Equal(204, (int)(await pos.PosAsync("POST", "/api/v1/pos/agents/logout", pump.DeviceToken, pump.Session, null)).StatusCode);
        var login = await Json(await pos.AgentLoginAsync(pump.DeviceToken, pump.WorkerId, pump.Pin), 200);
        await pos.Owner.ExecuteAsync("update agent_sessions set expires_at = now() - interval '1 second' where token_hash = encode(sha256(convert_to(@t, 'UTF8')), 'hex')",
            new { t = login["sessionToken"]!.GetValue<string>() });
        await Json(await pos.AgentLoginAsync(pump.DeviceToken, pump.WorkerId, pump.Pin), 200);

        var shifts = await Json(await OpsAsync("GET", $"/api/v1/ops/shifts?deviceId={pump.DeviceId}"), 200);
        var items = shifts["items"]!.AsArray();
        // Newest first: open; ended by sign-in elsewhere though already past its end (revocation wins); signed out.
        Assert.Equal([null, "SIGNED_IN_ELSEWHERE", "SIGNED_OUT"], items.Select(s => s!["endReason"]?.GetValue<string>()));
        Assert.Null(items[0]!["endedAt"]);
        Assert.Equal(pump.WorkerId, items[0]!["workerId"]!.GetValue<string>());
        Assert.Equal(pump.DeviceCode, items[0]!["deviceCode"]!.GetValue<string>());

        await pos.Owner.ExecuteAsync("update agent_sessions set expires_at = now() - interval '1 second' where device_id = @d::uuid and revoked_at is null",
            new { d = pump.DeviceId });
        var expired = await Json(await OpsAsync("GET", $"/api/v1/ops/shifts?deviceId={pump.DeviceId}&limit=1"), 200);
        Assert.Equal("EXPIRED", expired["items"]![0]!["endReason"]!.GetValue<string>());
        Assert.NotNull(expired["nextCursor"]);

        var manager = (await pos.Id.SignInAsync(await pos.Id.SeedAsync(Role.STATION_MANAGER, Scope.Operator(Guid.Parse(pump.OperatorId)), false))).AccessToken;
        Assert.Equal(3, (await Json(await pos.Id.AuthedAsync("GET", "/api/v1/station/shifts", manager), 200))["items"]!.AsArray().Count);
        var stranger = (await pos.Id.SignInAsync(await pos.Id.SeedAsync(Role.STATION_MANAGER, Scope.Operator(Guid.Parse(await pos.OperatorAsync())), false))).AccessToken;
        Assert.Empty((await Json(await pos.Id.AuthedAsync("GET", $"/api/v1/station/shifts?deviceId={pump.DeviceId}", stranger), 200))["items"]!.AsArray());
    }

    private async Task<string> StationAsync(string @operator)
    {
        var created = await OpsAsync("POST", "/api/v1/ops/stations",
            $$"""{"operatorId":"{{@operator}}","station":{"nameAr":"محطة","nameEn":"Station","latitude":30.0444,"longitude":31.2357,"connectivity":"NORMAL","fuelTypes":["GAS_92","DIESEL"]} }""");
        var body = await Json(created, 201);
        Assert.Equal("Africa/Cairo", body["timeZone"]!.GetValue<string>());
        return body["id"]!.GetValue<string>();
    }
}
