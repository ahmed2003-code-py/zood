using System.Text.Json;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Support;

namespace Zawed.Tests.Modules.Tenancy;

/// <summary>
/// What the tenancy and pricing tests need from the Java <c>IdentityTestSupport</c>: a seeded user of a role and
/// scope, and requests sent as that user. Until the identity module lands, the token is minted directly for the
/// principal (<see cref="ProbeApi.TokenFor"/>); after it, these helpers sign in through the API.
/// </summary>
public static class MasterDataApi
{
    /// <summary>A user of <paramref name="role"/> in <paramref name="scope"/> (a new company or operator id when omitted).</summary>
    public static async Task<ZawedPrincipal> SeedAsync(this ProbeApi api, Role role, Scope? scope = null)
    {
        scope ??= role.IsPlatform() ? Scope.Platform : new Scope(role.ScopeType(), Guid.CreateVersion7());
        var (id, _) = await Seed.UserAsync(api.Owner, role.ToString(), scope.Type.ToString(), scope.Id);
        return new ZawedPrincipal(id, Guid.CreateVersion7(), role, scope, false);
    }

    public static Task<HttpResponseMessage> SendAsync(this ProbeApi api, ZawedPrincipal user, HttpMethod method, string path,
        string? json = null, string? idempotencyKey = null)
    {
        var request = api.As(user, method, path, json);
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }
        return api.Client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> GetAsync(this ProbeApi api, ZawedPrincipal user, string path) =>
        api.SendAsync(user, HttpMethod.Get, path);

    public static Task<HttpResponseMessage> PostAsync(this ProbeApi api, ZawedPrincipal user, string path, string json, string? idempotencyKey = null) =>
        api.SendAsync(user, HttpMethod.Post, path, json, idempotencyKey);

    public static Task<HttpResponseMessage> PutAsync(this ProbeApi api, ZawedPrincipal user, string path, string json) =>
        api.SendAsync(user, HttpMethod.Put, path, json);

    /// <summary>A company as the Java <c>IdentityTestSupport.seedCompany</c> writes it.</summary>
    public static async Task<Guid> SeedCompanyAsync(this ProbeApi api, long lowBalanceThresholdPiastres = 0)
    {
        var id = Guid.CreateVersion7();
        await api.Owner.ExecuteAsync("""
            insert into companies (id, legal_name, name_ar, name_en, status, cooldown_minutes, low_balance_threshold_piastres,
                                   created_at, updated_at)
            values (@id, @name, 'شركة', @name, 'ACTIVE', 180, @lowBalanceThresholdPiastres, now(), now())
            """, new { id, name = "Co " + id.ToString()[..8], lowBalanceThresholdPiastres });
        return id;
    }

    /// <summary>An operator and one station (Java <c>PricesTests.seedStation</c>).</summary>
    public static async Task<Guid> SeedStationAsync(this ProbeApi api, string fuelTypes = "{CNG}")
    {
        var operatorId = Guid.CreateVersion7();
        var station = Guid.CreateVersion7();
        await api.Owner.ExecuteAsync("""
            insert into station_operators (id, legal_name, name_ar, name_en, status, created_at, updated_at)
            values (@operatorId, 'Op', 'مشغل', 'Op', 'ACTIVE', now(), now());
            insert into stations (id, operator_id, name_ar, name_en, time_zone, connectivity, fuel_types, status, created_at, updated_at)
            values (@station, @operatorId, 'محطة', 'Station', 'Africa/Cairo', 'NORMAL', @fuelTypes::text[], 'ACTIVE', now(), now());
            """, new { operatorId, station, fuelTypes });
        return station;
    }

    /// <summary>Nine random digits (Java <c>CompanyContactTests.taxDigits</c>).</summary>
    public static string TaxDigits() => Random.Shared.Next(1_000_000_000).ToString("D9", System.Globalization.CultureInfo.InvariantCulture);

    public static async Task<string?> CodeAsync(this HttpResponseMessage response) =>
        (await response.JsonAsync()).TryGetProperty("code", out var code) ? code.GetString() : null;

    public static string? Text(this JsonElement json, string property) =>
        json.GetProperty(property).ValueKind == JsonValueKind.Null ? null : json.GetProperty(property).GetString();

    public static List<string?> Items(this JsonElement page, string property) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty(property).GetString()).ToList();

    /// <summary>Asserts <c>400 VALIDATION_FAILED</c> naming exactly <paramref name="field"/>.</summary>
    public static async Task AssertInvalidAsync(HttpResponseMessage response, string field)
    {
        var body = await Http.AssertErrorAsync(response, 400, "VALIDATION_FAILED");
        Assert.Equal([field], body.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("field").GetString()));
    }
}
