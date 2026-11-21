using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Zawed.Tests.Support;

/// <summary>
/// The whole application on a real PostgreSQL (Java <c>support.IntegrationTest</c>): the real pipeline,
/// filters, authentication and migrations, served in memory by <c>TestServer</c>. Requests go through the
/// HTTP pipeline end to end, so what is asserted is what a client receives.
/// <para>Settings are passed as host settings (<see cref="Settings"/>), so <c>Program</c> sees them before
/// anything is built. The environment is <c>TEST</c>, like the Java suite.</para>
/// </summary>
public class ZawedApiFactory : WebApplicationFactory<Program>
{
    private readonly string database;

    public ZawedApiFactory(string database = TestDatabase.SharedName, IDictionary<string, string?>? settings = null)
    {
        this.database = database;
        Settings = new Dictionary<string, string?>
        {
            ["Zawed:Environment"] = "TEST",
            ["Database:Name"] = database,
            ["Database:User"] = TestDatabase.User,
            ["Database:Password"] = TestDatabase.Password,
            ["Database:MaxPoolSize"] = "20",
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            Settings[key] = value;
        }
    }

    public Dictionary<string, string?> Settings { get; }

    /// <summary>When set, replaces the clock (ADR 0007: an injected clock is the only source of "now").</summary>
    public FakeTimeProvider? Clock { get; init; }

    public Owner Owner => new(TestDatabase.OwnerConnectionStringAsync(database).GetAwaiter().GetResult());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Database:Host", TestDatabase.HostAsync().GetAwaiter().GetResult());
        builder.UseSetting("Database:Port", TestDatabase.PortAsync().GetAwaiter().GetResult().ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }
        if (Clock is not null)
        {
            builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock)));
        }
    }
}

/// <summary>
/// The shared application for a test class (xUnit class fixture): started once, on the shared database.
/// </summary>
public sealed class SharedApi : IAsyncLifetime
{
    public ZawedApiFactory Factory { get; } = new();

    public HttpClient Client { get; private set; } = null!;

    public Task InitializeAsync()
    {
        Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await Factory.DisposeAsync();
}
