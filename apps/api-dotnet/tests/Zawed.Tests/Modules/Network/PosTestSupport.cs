using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Support;

namespace Zawed.Tests.Modules.Network;

/// <summary>A device with its key, its token, and an agent signed in on it (Java <c>PosTestSupport.Pump</c>).</summary>
public sealed record Pump(string DeviceId, string DeviceCode, string StationId, string OperatorId, ECDsa Keys, string DeviceToken,
    string WorkerId, string Pin, string Session);

/// <summary>A device registered at a station, not yet activated (Java's <c>String[] {id, code, activationCode}</c>).</summary>
public sealed record DeviceRegistration(string Id, string Code, string ActivationCode);

/// <summary>
/// Sets up a working pump through the real API, the way Operations and the installer would (Java <c>PosTestSupport</c>, the
/// network half): an operator and station, a device activated with its own key pair, an agent who has changed the PIN.
/// Keys and signatures are made as the web POS makes them with WebCrypto: a P-256 key exported as SPKI, and an ECDSA
/// SHA-256 signature returned as r‖s (64 bytes) and converted to DER (<c>apps/web/src/pos/live/device-key.ts</c>).
/// Create one per test: it resets the in-memory rate limits.
/// </summary>
public sealed class PosTestSupport
{
    public PosTestSupport(ZawedApiFactory factory)
    {
        Id = new IdentityTestSupport(factory);
        Ops = new Lazy<Task<string>>(async () => (await Id.SignInAsync(await Id.SeedAsync(Role.OPS))).AccessToken);
    }

    public IdentityTestSupport Id { get; }

    public Owner Owner => Id.Owner;

    /// <summary>An Ops user's access token, signed in once per test.</summary>
    public Lazy<Task<string>> Ops { get; }

    // ------------------------------------------------------------------------------------------------- network

    public async Task<string> OperatorAsync()
    {
        var created = await Id.AuthedAsync("POST", "/api/v1/ops/operators", await Ops.Value,
            $$"""{"legalName":"Op {{Guid.CreateVersion7()}}","nameAr":"مشغل","nameEn":"Operator"}""");
        return await IdOf(created, 201, "id");
    }

    public async Task<string> StationAsync(string @operator, params string[] fuels)
    {
        var list = fuels.Length == 0 ? "\"GAS_92\",\"DIESEL\"" : string.Join(",", fuels.Select(f => $"\"{f}\""));
        var created = await Id.AuthedAsync("POST", "/api/v1/ops/stations", await Ops.Value,
            $$"""{"operatorId":"{{@operator}}","station":{"nameAr":"محطة الاختبار","nameEn":"Test Station","connectivity":"NORMAL","fuelTypes":[{{list}}]} }""");
        return await IdOf(created, 201, "id");
    }

    public async Task<DeviceRegistration> RegisterDeviceAsync(string station)
    {
        var registered = await Id.AuthedAsync("POST", "/api/v1/ops/devices", await Ops.Value, $$"""{"stationId":"{{station}}"}""");
        var body = await Json(registered, 201);
        return new DeviceRegistration(body["device"]!["id"]!.GetValue<string>(), body["device"]!["code"]!.GetValue<string>(),
            body["activationCode"]!.GetValue<string>());
    }

    /// <summary>A new agent at the station: (workerId, pin).</summary>
    public async Task<(string WorkerId, string Pin)> AgentAsync(string station)
    {
        var created = await Id.AuthedAsync("POST", "/api/v1/ops/agents", await Ops.Value,
            $$"""{"stationId":"{{station}}","fullName":"Agent {{Guid.CreateVersion7().ToString()[..6]}}"}""");
        var body = await Json(created, 201);
        return (body["agent"]!["workerId"]!.GetValue<string>(), body["pin"]!.GetValue<string>());
    }

    // ----------------------------------------------------------------------------------------------- the device

    /// <summary>A P-256 key pair, as <c>crypto.subtle.generateKey({name: 'ECDSA', namedCurve: 'P-256'})</c>.</summary>
    public static ECDsa KeyPair() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>The public key as <c>exportKey('spki')</c>, base64.</summary>
    public static string PublicKey(ECDsa keys) => Convert.ToBase64String(keys.ExportSubjectPublicKeyInfo());

    /// <summary>WebCrypto's ECDSA signature (r‖s) over the UTF-8 message, converted to DER and base64 as the web POS does.</summary>
    public static string Sign(ECDsa keys, string message) =>
        Convert.ToBase64String(RawToDer(keys.SignData(Encoding.UTF8.GetBytes(message), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));

    /// <summary>The web's <c>rawToDer</c>: SEQUENCE { INTEGER r, INTEGER s }, each minimal and positive.</summary>
    public static byte[] RawToDer(byte[] raw)
    {
        Assert.Equal(64, raw.Length);
        static List<byte> Integer(ReadOnlySpan<byte> bytes)
        {
            var start = 0;
            while (start < bytes.Length - 1 && bytes[start] == 0)
            {
                start++;
            }
            var trimmed = bytes[start..].ToArray();
            var result = new List<byte> { 0x02 };
            if ((trimmed[0] & 0x80) != 0)
            {
                result.Add((byte)(trimmed.Length + 1));
                result.Add(0x00);
            }
            else
            {
                result.Add((byte)trimmed.Length);
            }
            result.AddRange(trimmed);
            return result;
        }
        var r = Integer(raw.AsSpan(0, 32));
        var s = Integer(raw.AsSpan(32));
        return [0x30, (byte)(r.Count + s.Count), .. r, .. s];
    }

    public Task<HttpResponseMessage> ActivateAsync(string deviceCode, string activationCode, ECDsa keys) =>
        Id.PostAsync("/api/v1/pos/activate", $$"""{"deviceCode":"{{deviceCode}}","activationCode":"{{activationCode}}","publicKey":"{{PublicKey(keys)}}"}""");

    public Task<HttpResponseMessage> TokenRequestAsync(string deviceId, ECDsa keys, long timestamp, string nonce)
    {
        var message = $"zawed-device-token\n{deviceId}\n{timestamp}\n{nonce}";
        return Id.PostAsync("/api/v1/pos/token",
            $$"""{"deviceId":"{{deviceId}}","timestamp":{{timestamp}},"nonce":"{{nonce}}","signature":"{{Sign(keys, message)}}"}""");
    }

    public async Task<string> DeviceTokenAsync(string deviceId, ECDsa keys)
    {
        var issued = await TokenRequestAsync(deviceId, keys, Now(), Nonce());
        return (await Json(issued, 200))["token"]!.GetValue<string>();
    }

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>As the web POS: <c>crypto.randomUUID()</c>.</summary>
    public static string Nonce() => Guid.NewGuid().ToString();

    /// <summary>Any POS call: the device token, and the agent session when there is one.</summary>
    public Task<HttpResponseMessage> PosAsync(string method, string path, string? deviceToken, string? session, string? json,
        string? contract = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (deviceToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);
        }
        if (session is not null)
        {
            request.Headers.Add("X-Agent-Session", session);
        }
        if (contract is not null)
        {
            request.Headers.Add("X-Zawed-Contract", contract);
        }
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        return Id.Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> AgentLoginAsync(string deviceToken, string workerId, string pin) =>
        PosAsync("POST", "/api/v1/pos/agents/login", deviceToken, null, $$"""{"workerId":"{{workerId}}","pin":"{{pin}}"}""");

    /// <summary>A pump ready to work at a new station of a new operator.</summary>
    public async Task<Pump> PumpAsync(params string[] fuels)
    {
        var @operator = await OperatorAsync();
        return await PumpAtAsync(@operator, await StationAsync(@operator, fuels));
    }

    public async Task<Pump> PumpAtAsync(string @operator, string station)
    {
        var device = await RegisterDeviceAsync(station);
        var keys = KeyPair();
        await Json(await ActivateAsync(device.Code, device.ActivationCode, keys), 200);
        var token = await DeviceTokenAsync(device.Id, keys);
        var (workerId, pin) = await AgentAsync(station);
        var session = (await Json(await AgentLoginAsync(token, workerId, pin), 200))["sessionToken"]!.GetValue<string>();
        const string newPin = "482915";
        var changed = await PosAsync("POST", "/api/v1/pos/agents/pin", token, session, $$"""{"currentPin":"{{pin}}","newPin":"{{newPin}}"}""");
        Assert.True((int)changed.StatusCode == 204, await changed.Content.ReadAsStringAsync());
        // Must-change-PIN is read on every request, so the same session may now work.
        return new Pump(device.Id, device.Code, station, @operator, keys, token, workerId, newPin, session);
    }

    /// <summary>A fueling request as the POS sends it. Until wave 3A serves fuelings, the chain is what answers it.</summary>
    public static string FuelingBody() =>
        $$"""{"clientTxnId":"{{Guid.NewGuid()}}","tagUid":"04A1B2C3D4E5F6","volumeMl":1000,"deviceAt":"{{DateTimeOffset.UtcNow:O}}","receiptLocale":"ar"}""";

    public Task<HttpResponseMessage> FuelAsync(string deviceToken, string? session) =>
        PosAsync("POST", "/api/v1/pos/fuelings", deviceToken, session, FuelingBody());

    // ------------------------------------------------------------------------------------------------- helpers

    public static async Task<JsonNode> Json(HttpResponseMessage response, int status)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True((int)response.StatusCode == status, $"{(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!;
    }

    private static async Task<string> IdOf(HttpResponseMessage response, int status, string property) =>
        (await Json(response, status))[property]!.GetValue<string>();

    public static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return text.TrimStart().StartsWith('{') ? JsonNode.Parse(text)?["code"]?.GetValue<string>() : null;
    }
}
