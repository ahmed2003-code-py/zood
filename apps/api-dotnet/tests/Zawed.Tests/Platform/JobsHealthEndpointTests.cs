using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zawed.Api.Platform.Jobs;
using Zawed.Tests.Support;

namespace Zawed.Tests.Platform;

/// <summary>
/// The job health end to end, over HTTP (ADR 0028; Java <c>JobsHealthEndpointTests</c>): a job with no success in its
/// window turns the public health answer DOWN (503), a failing one WARN (200), while the readiness and liveness probes
/// stay UP. The job states are given (a stand-in for <see cref="IJobsHealth"/>), in an application of each test's own,
/// so the shared application's health answer and its 30-second cache are never touched.
/// </summary>
public sealed class JobsHealthEndpointTests : IAsyncLifetime
{
    private readonly GivenStates given = new();
    private readonly Factory factory;
    private HttpClient client = null!;

    public JobsHealthEndpointTests() => factory = new Factory(given);

    public Task InitializeAsync()
    {
        client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await factory.DisposeAsync();

    [Fact]
    public async Task AnOverdueJobTurnsTheHealthDownButNotTheProbes()
    {
        given.States = [Status("ledger-reconciliation", JobState.OVERDUE), Status("nonce-retention", JobState.OK)];

        var overall = await client.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, overall.StatusCode);
        Assert.Equal("DOWN", (await overall.JsonAsync()).GetProperty("status").GetString());
        var body = await overall.Content.ReadAsStringAsync();
        Assert.DoesNotContain("ledger-reconciliation", body);
        Assert.DoesNotContain("components", body);

        await AssertProbesUp();
    }

    [Fact]
    public async Task AFailingJobTurnsTheHealthWarnWhichStillAnswers200()
    {
        given.States = [Status("session-retention", JobState.FAILING), Status("nonce-retention", JobState.OK)];

        var overall = await client.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, overall.StatusCode);
        Assert.Equal("WARN", (await overall.JsonAsync()).GetProperty("status").GetString());

        await AssertProbesUp();
    }

    [Fact]
    public async Task HealthyAndPendingJobsLeaveTheHealthUp()
    {
        given.States = [Status("ledger-reconciliation", JobState.PENDING), Status("nonce-retention", JobState.OK)];

        var overall = await client.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, overall.StatusCode);
        Assert.Equal("UP", (await overall.JsonAsync()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task AJobsAnswerThatCannotBeHadIsUnknownAndLeavesTheHealthUp()
    {
        given.Failure = new Npgsql.NpgsqlException("down");

        var overall = await client.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, overall.StatusCode);
        Assert.Equal("UP", (await overall.JsonAsync()).GetProperty("status").GetString());
    }

    private async Task AssertProbesUp()
    {
        foreach (var probe in new[] { "/api/v1/health/readiness", "/api/v1/health/liveness" })
        {
            var response = await client.GetAsync(probe);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("UP", (await response.JsonAsync()).GetProperty("status").GetString());
        }
    }

    private static JobStatus Status(string job, JobState state) =>
        new(new Job(job, TimeSpan.FromHours(1)), state, DateTimeOffset.UnixEpoch, null, null, null, null, null, null);

    private sealed class GivenStates : IJobsHealth
    {
        public IReadOnlyList<JobStatus> States { get; set; } = [];

        public Exception? Failure { get; set; }

        public Task<IReadOnlyList<JobStatus>> StatusesAsync(CancellationToken ct) =>
            Failure is { } failure ? Task.FromException<IReadOnlyList<JobStatus>>(failure) : Task.FromResult(States);
    }

    private sealed class Factory(GivenStates given) : ZawedApiFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton<IJobsHealth>(given)));
        }
    }
}
