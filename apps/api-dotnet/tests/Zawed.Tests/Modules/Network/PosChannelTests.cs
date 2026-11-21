using System.Security.Cryptography;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Network.PosTestSupport;

namespace Zawed.Tests.Modules.Network;

/// <summary>
/// The device channel (B4 contract §1, ADR 0010; Java <c>PosChannelTests</c>, the channel half): activation, device
/// tokens, agent sign-in, and the separation of device and user credentials. Where Java probed the agent gate with a
/// fueling, the same request is sent here: the POS chain answers it before any controller, and once the chain lets it
/// through, the fueling route (wave 3A) is not served yet, so "past the gate" is a 404 instead of Java's
/// <c>TAG_NOT_RECOGNISED</c>.
/// </summary>
public sealed class PosChannelTests(SharedApi api) : IClassFixture<SharedApi>
{
    private readonly PosTestSupport pos = new(api.Factory);

    // ---------------------------------------------------------------------------------------------- activation

    [Fact]
    public async Task AnActivationCodeWorksOnceAndBindsTheDeviceKey()
    {
        var device = await pos.RegisterDeviceAsync(await pos.StationAsync(await pos.OperatorAsync()));
        using var keys = KeyPair();

        var activated = await pos.ActivateAsync(device.Code, device.ActivationCode, keys);
        var body = await Json(activated, 200);
        Assert.Contains("no-store", activated.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        Assert.Equal(device.Id, body["deviceId"]!.GetValue<string>());
        Assert.Equal(device.Code, body["deviceCode"]!.GetValue<string>());
        Assert.Equal("ACTIVE", await pos.Owner.QueryOneAsync<string>("select status from pos_devices where id = @id::uuid", new { id = device.Id }));
        Assert.Null(await pos.Owner.QueryOneAsync<string>("select activation_code_hash from pos_devices where id = @id::uuid", new { id = device.Id }));
        // The stored key is the SPKI the device sent, re-encoded: the same bytes for a P-256 key.
        Assert.Equal(keys.ExportSubjectPublicKeyInfo(),
            await pos.Owner.QueryOneAsync<byte[]>("select public_key from pos_devices where id = @id::uuid", new { id = device.Id }));

        // Used: the same code, even with the same key, is refused; so is another key.
        Assert.Equal("ACTIVATION_FAILED", await CodeAsync(await pos.ActivateAsync(device.Code, device.ActivationCode, keys)));
        using var other = KeyPair();
        Assert.Equal("ACTIVATION_FAILED", await CodeAsync(await pos.ActivateAsync(device.Code, device.ActivationCode, other)));
        Assert.Equal(1L, await pos.Owner.QueryOneAsync<long>(
            "select count(*) from audit_log where entity_id = @id::uuid and action = 'DEVICE_ACTIVATED'", new { id = device.Id }));
    }

    [Fact]
    public async Task FiveWrongCodesKillTheActivationCode()
    {
        var device = await pos.RegisterDeviceAsync(await pos.StationAsync(await pos.OperatorAsync()));
        using var keys = KeyPair();
        for (var i = 0; i < 5; i++)
        {
            await Http.AssertErrorAsync(await pos.ActivateAsync(device.Code, $"WRONGCODE{i}XY", keys), 401, "ACTIVATION_FAILED");
        }
        // The attempts outlived their 401s: each was counted in a transaction of its own.
        Assert.Equal(5, await pos.Owner.QueryOneAsync<int>("select activation_attempts from pos_devices where id = @id::uuid", new { id = device.Id }));
        Assert.Equal("ACTIVATION_FAILED", await CodeAsync(await pos.ActivateAsync(device.Code, device.ActivationCode, keys))); // the right code, too late
    }

    [Fact]
    public async Task AnUnknownDeviceAndAnExpiredCodeAnswerExactlyLikeAWrongCode()
    {
        var device = await pos.RegisterDeviceAsync(await pos.StationAsync(await pos.OperatorAsync()));
        await pos.Owner.ExecuteAsync("update pos_devices set activation_expires_at = now() - interval '1 second' where id = @id::uuid", new { id = device.Id });
        using var keys = KeyPair();
        var expired = await pos.ActivateAsync(device.Code, device.ActivationCode, keys);
        var unknown = await pos.ActivateAsync("POS-NOSUCH00", device.ActivationCode, keys);
        var expiredBody = await Http.AssertErrorAsync(expired, 401, "ACTIVATION_FAILED");
        var unknownBody = await Http.AssertErrorAsync(unknown, 401, "ACTIVATION_FAILED");
        Assert.Equal(expiredBody.GetProperty("message").GetString(), unknownBody.GetProperty("message").GetString());
    }

    [Fact]
    public async Task OnlyAP256KeyIsAccepted()
    {
        var device = await pos.RegisterDeviceAsync(await pos.StationAsync(await pos.OperatorAsync()));
        using var rsa = RSA.Create(2048);
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var p256 = KeyPair();
        var compressed = Compressed(p256);
        foreach (var key in new[]
        {
            Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()), Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(compressed), "not base64!", "",
        })
        {
            var response = await pos.Id.PostAsync("/api/v1/pos/activate",
                $$"""{"deviceCode":"{{device.Code}}","activationCode":"{{device.ActivationCode}}","publicKey":"{{key}}"}""");
            if (key.Length == 0)
            {
                await Http.AssertErrorAsync(response, 400, "VALIDATION_FAILED"); // @NotBlank, before the service
                continue;
            }
            var refused = await Http.AssertErrorAsync(response, 400, "INVALID_DEVICE_KEY");
            Assert.Equal("publicKey", refused.GetProperty("field").GetString());
        }
        // The code was not spent on a bad key.
        Assert.Equal(200, (int)(await pos.ActivateAsync(device.Code, device.ActivationCode, p256)).StatusCode);
    }

    [Fact]
    public async Task TheCodesAreReadAsTypedOnAKeypad()
    {
        // Java strips and upper-cases both codes before it looks them up.
        var device = await pos.RegisterDeviceAsync(await pos.StationAsync(await pos.OperatorAsync()));
        using var keys = KeyPair();
        await Json(await pos.ActivateAsync($" {device.Code.ToLowerInvariant()} ", $" {device.ActivationCode.ToLowerInvariant()}\\t", keys), 200);
    }

    // ------------------------------------------------------------------------------------------- device tokens

    [Fact]
    public async Task ATokenNeedsTheDeviceKeyAFreshTimestampAndANewNonce()
    {
        var device = await pos.RegisterDeviceAsync(await pos.StationAsync(await pos.OperatorAsync()));
        using var keys = KeyPair();
        await Json(await pos.ActivateAsync(device.Code, device.ActivationCode, keys), 200);
        var now = Now();

        var nonce = Nonce();
        var issued = await pos.TokenRequestAsync(device.Id, keys, now, nonce);
        var body = await Json(issued, 200);
        Assert.Contains("no-store", issued.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        Assert.True(DateTimeOffset.Parse(body["expiresAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture)
            > DateTimeOffset.UtcNow.AddMinutes(14));

        await Http.AssertErrorAsync(await pos.TokenRequestAsync(device.Id, keys, now, nonce), 401, "DEVICE_REPLAY");

        using var other = KeyPair();
        await Http.AssertErrorAsync(await pos.TokenRequestAsync(device.Id, other, now, Nonce()), 401, "DEVICE_UNAUTHENTICATED");

        var stale = await Http.AssertErrorAsync(await pos.TokenRequestAsync(device.Id, keys, now - 600, Nonce()), 401, "DEVICE_CLOCK_SKEW");
        Assert.True(stale.GetProperty("params").GetProperty("serverTime").GetInt64() >= now);
        await Http.AssertErrorAsync(await pos.TokenRequestAsync(device.Id, keys, now + 600, Nonce()), 401, "DEVICE_CLOCK_SKEW");
        // Inside the window either way.
        await Json(await pos.TokenRequestAsync(device.Id, keys, now - 290, Nonce()), 200);
        await Json(await pos.TokenRequestAsync(device.Id, keys, now + 290, Nonce()), 200);
    }

    [Fact]
    public async Task OnlyADerSignatureOverTheExactMessageCounts()
    {
        var device = await pos.RegisterDeviceAsync(await pos.StationAsync(await pos.OperatorAsync()));
        using var keys = KeyPair();
        await Json(await pos.ActivateAsync(device.Code, device.ActivationCode, keys), 200);
        var now = Now();

        // .NET's own DER encoder gives the same form as the web's rawToDer.
        var nonce = Nonce();
        var der = Convert.ToBase64String(keys.SignData(System.Text.Encoding.UTF8.GetBytes($"zawed-device-token\n{device.Id}\n{now}\n{nonce}"),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        await Json(await TokenWithSignatureAsync(device.Id, now, nonce, der), 200);

        // WebCrypto's raw r‖s, not converted, is not DER: refused. So is a signature over another message.
        nonce = Nonce();
        var raw = Convert.ToBase64String(keys.SignData(System.Text.Encoding.UTF8.GetBytes($"zawed-device-token\n{device.Id}\n{now}\n{nonce}"),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        await Http.AssertErrorAsync(await TokenWithSignatureAsync(device.Id, now, nonce, raw), 401, "DEVICE_UNAUTHENTICATED");
        nonce = Nonce();
        await Http.AssertErrorAsync(await TokenWithSignatureAsync(device.Id, now, nonce, Sign(keys, $"zawed-device-token\n{device.Id}\n{now + 1}\n{nonce}")),
            401, "DEVICE_UNAUTHENTICATED");
        // A short nonce fails validation before anything is checked.
        await Http.AssertErrorAsync(await TokenWithSignatureAsync(device.Id, now, "short", Sign(keys, "x")), 400, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task APendingDeviceGetsNoToken()
    {
        var device = await pos.RegisterDeviceAsync(await pos.StationAsync(await pos.OperatorAsync()));
        using var keys = KeyPair();
        await Http.AssertErrorAsync(await pos.TokenRequestAsync(device.Id, keys, Now(), Nonce()), 401, "DEVICE_UNAUTHENTICATED");
    }

    [Fact]
    public async Task DeviceAndUserTokensNeverStandInForEachOther()
    {
        var pump = await pos.PumpAsync();
        var ops = await pos.Ops.Value;
        // A user's access token on a POS route, and a device token on a user route.
        await Http.AssertErrorAsync(await pos.PosAsync("POST", "/api/v1/pos/agents/login", ops, null,
            $$"""{"workerId":"{{pump.WorkerId}}","pin":"{{pump.Pin}}"}"""), 401, "DEVICE_UNAUTHENTICATED");
        Assert.Equal(401, (int)(await pos.Id.AuthedAsync("GET", "/api/v1/auth/me", pump.DeviceToken)).StatusCode);
        Assert.Equal(401, (int)(await pos.Id.AuthedAsync("GET", "/api/v1/ops/fuelings", pump.DeviceToken)).StatusCode);
        Assert.Equal(401, (int)(await pos.Id.AuthedAsync("GET", "/api/v1/ops/operators", pump.DeviceToken)).StatusCode);
    }

    /// <summary>
    /// The 1A review: a user access token on a POS route never becomes a user principal there. Every POS operation of the
    /// Java document (served or not yet), for every role, answers as Java's POS chain does, 401 DEVICE_UNAUTHENTICATED, and
    /// the two public calls ignore the token and check their own proof.
    /// </summary>
    [Fact]
    public async Task AUserTokenOnAnyPosRouteIsRefusedAsAnUnknownDevice()
    {
        string[] routes =
        [
            "POST /api/v1/pos/agents/login", "POST /api/v1/pos/agents/pin", "POST /api/v1/pos/agents/logout", "POST /api/v1/pos/fuelings",
            $"POST /api/v1/pos/fuelings/{Guid.NewGuid()}/resolve", "GET /api/v1/pos/snapshot", "POST /api/v1/pos/sync",
            "GET /api/v1/pos/activate", "GET /api/v1/pos/no-such-route",
        ];
        foreach (var role in Enum.GetValues<Zawed.Api.Platform.Security.Role>())
        {
            pos.Id.Limiter.Reset();
            var token = (await pos.Id.SignInAsync(await pos.Id.SeedAsync(role))).AccessToken;
            foreach (var route in routes)
            {
                var (method, path) = (route[..route.IndexOf(' ')], route[(route.IndexOf(' ') + 1)..]);
                var response = await pos.PosAsync(method, path, token, null, method == "GET" ? null : "{}");
                Assert.True((int)response.StatusCode == 401 && await CodeAsync(response) == "DEVICE_UNAUTHENTICATED",
                    $"{role} {route}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            }
            // The credential calls check only their own proof: the user token is not even read.
            var activate = await pos.PosAsync("POST", "/api/v1/pos/activate", token, null,
                """{"deviceCode":"POS-NOSUCH00","activationCode":"ABCDEFGHJKLM","publicKey":"MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE"}""");
            Assert.NotEqual("UNAUTHENTICATED", await CodeAsync(activate));
        }
    }

    [Fact]
    public async Task RevokingADeviceStopsItsTokenAtOnce()
    {
        var pump = await pos.PumpAsync();
        Assert.Equal(200, (int)(await pos.Id.AuthedAsync("POST", $"/api/v1/ops/devices/{pump.DeviceId}/revoke", await pos.Ops.Value,
            """{"reason":"Stolen"}""")).StatusCode);
        await Http.AssertErrorAsync(await pos.FuelAsync(pump.DeviceToken, pump.Session), 401, "DEVICE_UNAUTHENTICATED");
        await Http.AssertErrorAsync(await pos.TokenRequestAsync(pump.DeviceId, pump.Keys, Now(), Nonce()), 401, "DEVICE_UNAUTHENTICATED");
    }

    [Fact]
    public async Task SuspendingTheOperatorStopsItsDevices()
    {
        var pump = await pos.PumpAsync();
        Assert.Equal(200, (int)(await pos.Id.AuthedAsync("POST", $"/api/v1/ops/operators/{pump.OperatorId}/status", await pos.Ops.Value,
            """{"status":"SUSPENDED","reason":"Contract"}""")).StatusCode);
        await Http.AssertErrorAsync(await pos.PosAsync("POST", "/api/v1/pos/agents/logout", pump.DeviceToken, pump.Session, null),
            401, "DEVICE_UNAUTHENTICATED");
        await Http.AssertErrorAsync(await pos.TokenRequestAsync(pump.DeviceId, pump.Keys, Now(), Nonce()), 401, "DEVICE_UNAUTHENTICATED");
    }

    [Fact]
    public async Task ADeviceTokenIsAShortLivedHs256TokenOfItsOwnKind()
    {
        var pump = await pos.PumpAsync();
        var parts = pump.DeviceToken.Split('.');
        Assert.Equal("""{"alg":"HS256"}""", System.Text.Encoding.UTF8.GetString(Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(parts[0])));
        var claims = System.Text.Json.Nodes.JsonNode.Parse(Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Decode(parts[1]))!;
        Assert.Equal("zawed-api", claims["iss"]!.GetValue<string>());
        Assert.Equal(pump.DeviceId, claims["sub"]!.GetValue<string>());
        Assert.Equal("device", claims["typ"]!.GetValue<string>());
        Assert.Equal(15 * 60, claims["exp"]!.GetValue<long>() - claims["iat"]!.GetValue<long>());

        // The same claims signed with the user tokens' key, or expired, are not a device token.
        var userKey = System.Text.Encoding.UTF8.GetBytes("local-only-jwt-secret-do-not-deploy-0123456789abcdef");
        await Http.AssertErrorAsync(await pos.PosAsync("POST", "/api/v1/pos/agents/logout", Resign(parts, claims, userKey), pump.Session, null),
            401, "DEVICE_UNAUTHENTICATED");
        var deviceKey = HMACSHA256.HashData(userKey, "zawed-device-token-v1"u8);
        await Json(await pos.PosAsync("POST", "/api/v1/pos/agents/login", Resign(parts, claims, deviceKey), null,
            $$"""{"workerId":"{{pump.WorkerId}}","pin":"{{pump.Pin}}"}"""), 200); // the derived key: what Java issues, accepted
        var expired = claims.DeepClone();
        expired["exp"] = DateTimeOffset.UtcNow.AddSeconds(-31).ToUnixTimeSeconds();
        await Http.AssertErrorAsync(await pos.PosAsync("POST", "/api/v1/pos/agents/login", Resign(parts, expired, deviceKey), null, "{}"),
            401, "DEVICE_UNAUTHENTICATED");
        var user = claims.DeepClone();
        user["typ"] = "user";
        await Http.AssertErrorAsync(await pos.PosAsync("POST", "/api/v1/pos/agents/login", Resign(parts, user, deviceKey), null, "{}"),
            401, "DEVICE_UNAUTHENTICATED");
    }

    [Fact]
    public async Task AnOlderTerminalIsToldToUpdateBeforeAnythingElse()
    {
        var pump = await pos.PumpAsync();
        foreach (var version in new[] { "0", "-1", "x", "1.5" })
        {
            var refused = await Http.AssertErrorAsync(await pos.PosAsync("POST", "/api/v1/pos/agents/logout", null, null, null, version),
                426, "CONTRACT_UPGRADE_REQUIRED");
            Assert.Equal(1, refused.GetProperty("params").GetProperty("minimum").GetInt32());
        }
        // The current contract, or none (the web simulator), goes on to the device check.
        await Http.AssertErrorAsync(await pos.PosAsync("POST", "/api/v1/pos/agents/logout", null, null, null, "1"), 401, "DEVICE_UNAUTHENTICATED");
        await Http.AssertErrorAsync(await pos.PosAsync("POST", "/api/v1/pos/agents/logout", null, null, null, " "), 401, "DEVICE_UNAUTHENTICATED");
        Assert.Equal(204, (int)(await pos.PosAsync("POST", "/api/v1/pos/agents/logout", pump.DeviceToken, pump.Session, null, "2")).StatusCode);
        // The credential calls are not behind the check.
        await Http.AssertErrorAsync(await pos.PosAsync("POST", "/api/v1/pos/activate", null, null,
            """{"deviceCode":"POS-NOSUCH00","activationCode":"ABCDEFGHJKLM","publicKey":"MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE"}""", "0"),
            400, "INVALID_DEVICE_KEY");
    }

    [Fact]
    public async Task ARunawayTerminalIsLimitedPerDevice()
    {
        var pump = await pos.PumpAsync();
        pos.Id.Limiter.Reset();
        for (var i = 0; i < 120; i++)
        {
            Assert.Equal(404, (int)(await pos.PosAsync("GET", "/api/v1/pos/no-such-route", pump.DeviceToken, pump.Session, null)).StatusCode);
        }
        var limited = await Http.AssertErrorAsync(await pos.PosAsync("GET", "/api/v1/pos/no-such-route", pump.DeviceToken, pump.Session, null),
            429, "RATE_LIMITED");
        Assert.True(limited.GetProperty("params").GetProperty("retryAfterSeconds").GetInt64() > 0);
    }

    // ------------------------------------------------------------------------------------------- agent sign-in

    [Fact]
    public async Task ANewAgentMustChangeThePinBeforeFueling()
    {
        var @operator = await pos.OperatorAsync();
        var station = await pos.StationAsync(@operator);
        var pump = await pos.PumpAtAsync(@operator, station);
        var (workerId, pin) = await pos.AgentAsync(station);
        var login = await pos.AgentLoginAsync(pump.DeviceToken, workerId, pin);
        var body = await Json(login, 200);
        Assert.Contains("no-store", login.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        Assert.True(body["agent"]!["mustChangePin"]!.GetValue<bool>());
        Assert.Equal(workerId, body["agent"]!["workerId"]!.GetValue<string>());
        var session = body["sessionToken"]!.GetValue<string>();

        await Http.AssertErrorAsync(await pos.FuelAsync(pump.DeviceToken, session), 403, "PIN_CHANGE_REQUIRED");

        await Http.AssertErrorAsync(await pos.PosAsync("POST", "/api/v1/pos/agents/pin", pump.DeviceToken, session,
            $$"""{"currentPin":"{{pin}}","newPin":"123456"}"""), 400, "PIN_TOO_WEAK");
        await Http.AssertErrorAsync(await pos.PosAsync("POST", "/api/v1/pos/agents/pin", pump.DeviceToken, session,
            $$"""{"currentPin":"{{pin}}","newPin":"{{pin}}"}"""), 400, "PIN_REUSED");
        Assert.Equal(204, (int)(await pos.PosAsync("POST", "/api/v1/pos/agents/pin", pump.DeviceToken, session,
            $$"""{"currentPin":"{{pin}}","newPin":"730518"}""")).StatusCode);
        var now = await pos.FuelAsync(pump.DeviceToken, session);
        Assert.Equal(404, (int)now.StatusCode); // past the PIN gate; the fueling itself comes with wave 3A
    }

    [Fact]
    public async Task ADeviceWithoutAnAgentCannotFuel()
    {
        var pump = await pos.PumpAsync();
        await Http.AssertErrorAsync(await pos.FuelAsync(pump.DeviceToken, null), 401, "AGENT_SESSION_REQUIRED");
        await Http.AssertErrorAsync(await pos.FuelAsync(pump.DeviceToken, "not-a-session"), 401, "AGENT_SESSION_INVALID");
        // The device alone may sign an agent in; the PIN change and sign-out need the agent.
        await Http.AssertErrorAsync(await pos.PosAsync("POST", "/api/v1/pos/agents/logout", pump.DeviceToken, null, null), 401, "AGENT_SESSION_REQUIRED");
        await Http.AssertErrorAsync(await pos.PosAsync("POST", "/api/v1/pos/agents/pin", pump.DeviceToken, null,
            """{"currentPin":"482915","newPin":"592047"}"""), 401, "AGENT_SESSION_REQUIRED");
    }

    [Fact]
    public async Task FiveWrongPinsLockTheAgent()
    {
        var pump = await pos.PumpAsync();
        for (var i = 0; i < 4; i++)
        {
            await Http.AssertErrorAsync(await pos.AgentLoginAsync(pump.DeviceToken, pump.WorkerId, "000000"), 401, "AGENT_SIGN_IN_FAILED");
        }
        var fifth = await Http.AssertErrorAsync(await pos.AgentLoginAsync(pump.DeviceToken, pump.WorkerId, "000000"), 423, "AGENT_LOCKED");
        Assert.InRange(fifth.GetProperty("params").GetProperty("retryAfterSeconds").GetInt64(), 899, 900);
        await Http.AssertErrorAsync(await pos.AgentLoginAsync(pump.DeviceToken, pump.WorkerId, pump.Pin), 423, "AGENT_LOCKED"); // the right PIN while locked
        Assert.Equal(1L, await pos.Owner.QueryOneAsync<long>(
            "select count(*) from audit_log where action = 'AGENT_LOCKED' and entity_id = (select id from agents where worker_id = @w)",
            new { w = pump.WorkerId }));
        Assert.Equal(4L, await pos.Owner.QueryOneAsync<long>(
            "select count(*) from audit_log where action = 'AGENT_PIN_FAILED' and entity_id = (select id from agents where worker_id = @w)",
            new { w = pump.WorkerId }));
    }

    [Fact]
    public async Task AnAgentSignsInOnlyAtTheirOwnStation()
    {
        var here = await pos.PumpAsync();
        var elsewhere = await pos.PumpAsync();
        await Http.AssertErrorAsync(await pos.AgentLoginAsync(here.DeviceToken, elsewhere.WorkerId, elsewhere.Pin), 401, "AGENT_SIGN_IN_FAILED");
        await Http.AssertErrorAsync(await pos.AgentLoginAsync(here.DeviceToken, "W0000000", "482915"), 401, "AGENT_SIGN_IN_FAILED");
        // A worker id is matched as typed on a keypad.
        await Json(await pos.AgentLoginAsync(here.DeviceToken, " " + here.WorkerId.ToLowerInvariant(), here.Pin + " "), 200);
    }

    [Fact]
    public async Task SigningInOnAnotherDeviceEndsTheFirstSession()
    {
        var @operator = await pos.OperatorAsync();
        var station = await pos.StationAsync(@operator);
        var first = await pos.PumpAtAsync(@operator, station);
        // A second device at the same station; the first pump's agent signs in there.
        var device = await pos.RegisterDeviceAsync(station);
        using var keys = KeyPair();
        await Json(await pos.ActivateAsync(device.Code, device.ActivationCode, keys), 200);
        var token = await pos.DeviceTokenAsync(device.Id, keys);
        await Json(await pos.AgentLoginAsync(token, first.WorkerId, first.Pin), 200);

        await Http.AssertErrorAsync(await pos.FuelAsync(first.DeviceToken, first.Session), 401, "AGENT_SESSION_INVALID");
        Assert.Equal(1L, await pos.Owner.QueryOneAsync<long>(
            "select count(*) from agent_sessions where agent_id = (select id from agents where worker_id = @w) and revoked_at is null",
            new { w = first.WorkerId }));
        Assert.Equal("SIGNED_IN_ELSEWHERE", await pos.Owner.QueryOneAsync<string>(
            "select revoke_reason from agent_sessions where token_hash = encode(sha256(convert_to(@t, 'UTF8')), 'hex')", new { t = first.Session }));
    }

    [Fact]
    public async Task ASuspendedAgentStopsAtOnceAndSignOutEndsTheSession()
    {
        var pump = await pos.PumpAsync();
        var agentId = await pos.Owner.QueryOneAsync<Guid>("select id from agents where worker_id = @w", new { w = pump.WorkerId });
        Assert.Equal(200, (int)(await pos.Id.AuthedAsync("POST", $"/api/v1/ops/agents/{agentId}/status", await pos.Ops.Value,
            """{"status":"SUSPENDED","reason":"Investigation"}""")).StatusCode);
        await Http.AssertErrorAsync(await pos.FuelAsync(pump.DeviceToken, pump.Session), 401, "AGENT_SESSION_INVALID");
        await Http.AssertErrorAsync(await pos.AgentLoginAsync(pump.DeviceToken, pump.WorkerId, pump.Pin), 403, "AGENT_DISABLED");

        var other = await pos.PumpAsync();
        Assert.Equal(204, (int)(await pos.PosAsync("POST", "/api/v1/pos/agents/logout", other.DeviceToken, other.Session, null)).StatusCode);
        await Http.AssertErrorAsync(await pos.FuelAsync(other.DeviceToken, other.Session), 401, "AGENT_SESSION_INVALID");
    }

    [Fact]
    public async Task WrongCurrentPinsOnAPinChangeCountTowardsTheLockout()
    {
        var pump = await pos.PumpAsync();
        for (var i = 0; i < 4; i++)
        {
            var wrong = await Http.AssertErrorAsync(await pos.PosAsync("POST", "/api/v1/pos/agents/pin", pump.DeviceToken, pump.Session,
                """{"currentPin":"000000","newPin":"592047"}"""), 401, "AGENT_SIGN_IN_FAILED");
            Assert.Equal("currentPin", wrong.GetProperty("field").GetString());
        }
        await Http.AssertErrorAsync(await pos.PosAsync("POST", "/api/v1/pos/agents/pin", pump.DeviceToken, pump.Session,
            """{"currentPin":"000000","newPin":"592047"}"""), 423, "AGENT_LOCKED");
        await Http.AssertErrorAsync(await pos.FuelAsync(pump.DeviceToken, pump.Session), 401, "AGENT_SESSION_INVALID"); // the lock ends the session
        await Http.AssertErrorAsync(await pos.AgentLoginAsync(pump.DeviceToken, pump.WorkerId, pump.Pin), 423, "AGENT_LOCKED");
    }

    [Fact]
    public async Task ReactivatingASuspendedAgentDoesNotReviveTheOldSession()
    {
        var pump = await pos.PumpAsync();
        var agentId = await pos.Owner.QueryOneAsync<Guid>("select id from agents where worker_id = @w", new { w = pump.WorkerId });
        foreach (var status in new[] { "SUSPENDED", "ACTIVE" })
        {
            Assert.Equal(200, (int)(await pos.Id.AuthedAsync("POST", $"/api/v1/ops/agents/{agentId}/status", await pos.Ops.Value,
                $$"""{"status":"{{status}}","reason":"Check"}""")).StatusCode);
        }
        await Http.AssertErrorAsync(await pos.FuelAsync(pump.DeviceToken, pump.Session), 401, "AGENT_SESSION_INVALID");
    }

    [Fact]
    public async Task AnOpsPinResetEndsTheSessionsAndForcesAChange()
    {
        var pump = await pos.PumpAsync();
        var agentId = await pos.Owner.QueryOneAsync<Guid>("select id from agents where worker_id = @w", new { w = pump.WorkerId });
        var reset = await Json(await pos.Id.AuthedAsync("POST", $"/api/v1/ops/agents/{agentId}/reset-pin", await pos.Ops.Value), 200);
        await Http.AssertErrorAsync(await pos.FuelAsync(pump.DeviceToken, pump.Session), 401, "AGENT_SESSION_INVALID");
        var login = await Json(await pos.AgentLoginAsync(pump.DeviceToken, pump.WorkerId, reset["pin"]!.GetValue<string>()), 200);
        Assert.True(login["agent"]!["mustChangePin"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ParallelSignInsLeaveOneLiveSession()
    {
        var pump = await pos.PumpAsync();
        var statuses = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
            (int)(await pos.AgentLoginAsync(pump.DeviceToken, pump.WorkerId, pump.Pin)).StatusCode));
        Assert.All(statuses, s => Assert.Equal(200, s));
        Assert.Equal(1L, await pos.Owner.QueryOneAsync<long>(
            "select count(*) from agent_sessions where agent_id = (select id from agents where worker_id = @w) and revoked_at is null",
            new { w = pump.WorkerId }));
    }

    [Fact]
    public async Task ParallelWrongPinsNeverGetMoreThanFiveTries()
    {
        var pump = await pos.PumpAsync();
        var codes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
            await CodeAsync(await pos.AgentLoginAsync(pump.DeviceToken, pump.WorkerId, "000000"))));
        Assert.Equal(4, codes.Count(c => c == "AGENT_SIGN_IN_FAILED"));
        Assert.Equal(4, codes.Count(c => c == "AGENT_LOCKED"));
    }

    [Fact]
    public async Task AgentSignInsAreLimitedPerDevice()
    {
        var pump = await pos.PumpAsync();
        pos.Id.Limiter.Reset();
        for (var i = 0; i < 10; i++)
        {
            await Http.AssertErrorAsync(await pos.AgentLoginAsync(pump.DeviceToken, "W0000000", "000000"), 401, "AGENT_SIGN_IN_FAILED");
        }
        await Http.AssertErrorAsync(await pos.AgentLoginAsync(pump.DeviceToken, "W0000000", "000000"), 429, "RATE_LIMITED");
    }

    [Fact]
    public async Task SessionTokensAreStoredOnlyAsHashes()
    {
        var pump = await pos.PumpAsync();
        Assert.Equal(0L, await pos.Owner.QueryOneAsync<long>("select count(*) from agent_sessions where token_hash = @t", new { t = pump.Session }));
        Assert.Equal(1L, await pos.Owner.QueryOneAsync<long>(
            "select count(*) from agent_sessions where token_hash = encode(sha256(convert_to(@t, 'UTF8')), 'hex')", new { t = pump.Session }));
        Assert.Equal(0L, await pos.Owner.QueryOneAsync<long>("select count(*) from audit_log where metadata::text like @t",
            new { t = "%" + pump.Session + "%" }));
    }

    [Fact]
    public async Task EveryPosRequestRecordsWhenTheDeviceAndTheAgentWereLastSeen()
    {
        var pump = await pos.PumpAsync();
        await pos.Owner.ExecuteAsync("update pos_devices set last_sync_at = now() - interval '10 minutes' where id = @id::uuid", new { id = pump.DeviceId });
        await pos.Owner.ExecuteAsync("update agent_sessions set last_seen_at = now() - interval '10 minutes' where token_hash = encode(sha256(convert_to(@t, 'UTF8')), 'hex')",
            new { t = pump.Session });
        await pos.FuelAsync(pump.DeviceToken, pump.Session);
        Assert.True(await pos.Owner.QueryOneAsync<bool>("select last_sync_at > now() - interval '1 minute' from pos_devices where id = @id::uuid", new { id = pump.DeviceId }));
        Assert.True(await pos.Owner.QueryOneAsync<bool>(
            "select last_seen_at > now() - interval '1 minute' from agent_sessions where token_hash = encode(sha256(convert_to(@t, 'UTF8')), 'hex')",
            new { t = pump.Session }));
    }

    // ------------------------------------------------------------------------------------------------- helpers

    private Task<HttpResponseMessage> TokenWithSignatureAsync(string deviceId, long timestamp, string nonce, string signature) =>
        pos.Id.PostAsync("/api/v1/pos/token",
            $$"""{"deviceId":"{{deviceId}}","timestamp":{{timestamp}},"nonce":"{{nonce}}","signature":"{{signature}}"}""");

    private static string Resign(string[] parts, System.Text.Json.Nodes.JsonNode claims, byte[] key)
    {
        var input = parts[0] + "." + Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(claims.ToJsonString());
        return input + "." + Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(HMACSHA256.HashData(key, System.Text.Encoding.ASCII.GetBytes(input)));
    }

    /// <summary>The same P-256 key with its point compressed (<c>02</c>/<c>03 x</c>): Java's EC key factory does not read it.</summary>
    private static byte[] Compressed(ECDsa key)
    {
        var point = key.ExportParameters(false).Q;
        byte[] compressedPoint = [(byte)((point.Y![^1] & 1) == 0 ? 0x02 : 0x03), .. point.X!];
        var writer = new System.Formats.Asn1.AsnWriter(System.Formats.Asn1.AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier("1.2.840.10045.2.1");
                writer.WriteObjectIdentifier("1.2.840.10045.3.1.7");
            }
            writer.WriteBitString(compressedPoint);
        }
        return writer.Encode();
    }
}
