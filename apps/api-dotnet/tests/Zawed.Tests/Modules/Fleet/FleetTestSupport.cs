using System.Net;
using System.Text.Json;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Modules.Tenancy;
using Zawed.Tests.Support;

namespace Zawed.Tests.Modules.Fleet;

/// <summary>What the fleet tests share (the helpers of the Java <c>FleetTests</c> and <c>FleetImportTests</c>).</summary>
public static class FleetTestSupport
{
    /// <summary>Runs <paramref name="n"/> calls at the same moment and returns their results (Java <c>Concurrently.run</c>).</summary>
    public static async Task<List<T>> RaceAsync<T>(int n, Func<int, Task<T>> call)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, n).Select(i => Task.Run(async () =>
        {
            await start.Task;
            return await call(i);
        })).ToList();
        start.SetResult();
        return [.. await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60))];
    }

    public static Task<HttpResponseMessage> CreateVehicleAsync(this ProbeApi api, ZawedPrincipal ops, Guid company, string plate,
        string tier = "NORMAL", long? ceiling = null, string fuel = "GAS_92", long tankMl = 50_000, string? fleetCode = null, int? year = 2022,
        string? departmentId = null) =>
        api.PostAsync(ops, "/api/v1/ops/vehicles", JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["companyId"] = company,
            ["vehicle"] = new Dictionary<string, object?>
            {
                ["departmentId"] = departmentId, ["plate"] = plate, ["make"] = "Toyota", ["model"] = "Corolla", ["year"] = year,
                ["fuelType"] = fuel, ["tankCapacityMl"] = tankMl, ["tier"] = tier, ["vipMonthlyCeilingMl"] = ceiling, ["fleetCode"] = fleetCode,
            },
        }));

    /// <summary>A vehicle of a new company; its id.</summary>
    public static async Task<string> VehicleAsync(this ProbeApi api, ZawedPrincipal ops)
    {
        var created = await api.CreateVehicleAsync(ops, await api.SeedCompanyAsync(), UniquePlate());
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        return (await created.JsonAsync()).Text("id")!;
    }

    public static Task<HttpResponseMessage> IssueTagAsync(this ProbeApi api, ZawedPrincipal ops, string vehicle, string uid) =>
        api.PostAsync(ops, $"/api/v1/ops/vehicles/{vehicle}/tags", JsonSerializer.Serialize(new { uid }));

    public static Task<HttpResponseMessage> TagStatusAsync(this ProbeApi api, ZawedPrincipal ops, string tag, string status) =>
        api.PostAsync(ops, $"/api/v1/ops/tags/{tag}/status", JsonSerializer.Serialize(new { status, reason = "test" }));

    public static string UniquePlate() => "TST " + Guid.NewGuid().ToString()[..8].ToUpperInvariant();

    public static string RandomUid() => Guid.NewGuid().ToString("N")[..14].ToUpperInvariant();

    /// <summary>Three letters Egyptian plates use, space separated.</summary>
    public static string RandomLetters()
    {
        const string alphabet = "أبجدرسصطعفقكلمنهوي";
        return string.Join(' ', Enumerable.Range(0, 3).Select(_ => alphabet[Random.Shared.Next(alphabet.Length)]));
    }

    /// <summary>An Egyptian-shaped plate, unique enough for a test run.</summary>
    public static string EgyptianPlate() => $"{1000 + Random.Shared.Next(8999)} {RandomLetters()}";

    public static string EasternDigits(string value) =>
        new([.. value.Select(c => c is >= '0' and <= '9' ? (char)('٠' + (c - '0')) : c)]);
}
