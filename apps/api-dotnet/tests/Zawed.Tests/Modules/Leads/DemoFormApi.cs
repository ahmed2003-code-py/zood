using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Zawed.Api.Modules.Leads;
using Zawed.Api.Platform.Crypto;
using Zawed.Tests.Support;

namespace Zawed.Tests.Modules.Leads;

/// <summary>
/// The tests that count against the demo form's shared limits run one class at a time: the global hourly cap
/// (<c>demo-all-hour</c>) is one counter for every caller.
/// </summary>
[CollectionDefinition(Name)]
public sealed class DemoFormCollection
{
    public const string Name = "demo form";
}

/// <summary>
/// The whole application for the demo-form tests (the Java <c>IntegrationTest</c> with <c>OutputCaptureExtension</c> and an
/// in-memory meter registry): every request comes from one address of its own (<see cref="Address"/>), so no other test
/// class spends its budget; the log lines and the <c>zawed.leads.demo.requests</c> counts of this application are kept.
/// <para><see cref="BehindProxy"/>: requests arrive from the platform's proxy (127.0.0.1) and the forwarded headers are read,
/// as the deployed profile reads them (the Java test's <c>server.forward-headers-strategy=native</c>).</para>
/// </summary>
public class DemoFormApi : IAsyncLifetime
{
    public const string FormPath = "/api/v1/public/demo-requests";

    private readonly ConcurrentDictionary<string, long> counts = new();
    private MeterListener? listener;

    public DemoFormApi() : this(false)
    {
    }

    protected DemoFormApi(bool behindProxy)
    {
        Address = $"198.18.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}";
        Factory = new DemoFactory(behindProxy ? "127.0.0.1" : Address, behindProxy, Logs);
    }

    public string Address { get; }
    public LogCapture Logs { get; } = new();
    public ZawedApiFactory Factory { get; }
    public HttpClient Client { get; private set; } = null!;
    public Owner Owner => Factory.Owner;

    public Task InitializeAsync()
    {
        Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        var meters = Factory.Services.GetRequiredService<IMeterFactory>();
        listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == LeadMetrics.MeterName && ReferenceEquals(instrument.Meter.Scope, meters))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome")
                {
                    counts.AddOrUpdate((string)tag.Value!, value, (_, before) => before + value);
                }
            }
        });
        listener.Start();
        _ = Factory.Services.GetRequiredService<LeadMetrics>(); // publish the counter now
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        listener?.Dispose();
        await Factory.DisposeAsync();
    }

    /// <summary>How many requests ended with <paramref name="outcome"/> in this application so far.</summary>
    public long Counted(string outcome) => counts.GetValueOrDefault(outcome);

    public Task<HttpResponseMessage> SubmitAsync(object form, string? forwardedFor = null) =>
        PostAsync(FormPath, JsonSerializer.Serialize(form), forwardedFor);

    public Task<HttpResponseMessage> PostAsync(string path, string body, string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }
        return Client.SendAsync(request);
    }

    /// <summary>
    /// Forgets this address's counters and the form's hourly cap (the Java test deleted every counter; here other classes
    /// keep theirs).
    /// </summary>
    public async Task ForgetCountersAsync()
    {
        var hasher = Factory.Services.GetRequiredService<LookupHasher>();
        await Owner.ExecuteAsync("""
            delete from rate_limit_counters
            where bucket = 'demo-all-hour' or (bucket = 'demo-ip-hour' and key_hash = @hour) or (bucket = 'demo-ip-day' and key_hash = @day)
            """, new { hour = hasher.Hash("rate-limit:demo-ip-hour", Address), day = hasher.Hash("rate-limit:demo-ip-day", Address) });
    }

    public string KeyHash(string bucket, string subject) => Factory.Services.GetRequiredService<LookupHasher>().Hash("rate-limit:" + bucket, subject);

    /// <summary>A valid form with its own phone, e-mail and company, shown ten seconds ago.</summary>
    public static Dictionary<string, object?> Form()
    {
        var token = Token();
        return new Dictionary<string, object?>
        {
            ["companyName"] = "Delta Logistics " + token,
            ["contactName"] = "Karim " + token,
            ["phone"] = "010" + Digits(),
            ["email"] = "karim-" + token + "@example.test",
            ["fleetSize"] = "20-49",
            ["locale"] = "ar",
            ["website"] = "",
            ["startedAt"] = DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeMilliseconds(),
        };
    }

    public static string Digits() => Random.Shared.Next(100_000_000).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);

    public static string Token() => Guid.NewGuid().ToString()[..8];

    public static string Arabic(string ascii) => new([.. ascii.Select(c => c is >= '0' and <= '9' ? (char)('٠' + (c - '0')) : c)]);

    private sealed class DemoFactory(string remote, bool behindProxy, LogCapture logs) : ZawedApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new ClientAddress(IPAddress.Parse(remote), behindProxy)));
        }
    }

    /// <summary>What the socket would report, before anything else; then, behind the proxy, its forwarded headers.</summary>
    private sealed class ClientAddress(IPAddress remote, bool behindProxy) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = remote;
                return nextMiddleware(context);
            });
            if (behindProxy)
            {
                app.UseForwardedHeaders();
            }
            next(app);
        };
    }
}

/// <summary>The same application with the platform's proxy in front of it.</summary>
public sealed class BehindProxyApi() : DemoFormApi(true);

/// <summary>Every log line the application writes, with its exception, for asserting what never reaches the log.</summary>
public sealed class LogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<string> lines = new();

    public string All => string.Join("\n", lines);

    public ILogger CreateLogger(string categoryName) => new Capturing(lines, categoryName);

    public void Dispose()
    {
    }

    private sealed class Capturing(ConcurrentQueue<string> lines, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
    }
}
