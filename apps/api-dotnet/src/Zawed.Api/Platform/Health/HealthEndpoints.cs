using Npgsql;
using Zawed.Api.Platform.Jobs;

namespace Zawed.Api.Platform.Health;

/// <summary>
/// The three public probes, with the Java API's status codes and bodies (Spring Boot actuator with
/// <c>show-details: never</c>, checked against zawed-dev on 2026-11-21): the body is only
/// <c>{"status":"UP"}</c>, in the actuator's media type.
/// <list type="bullet">
/// <item><c>/api/v1/health/liveness</c>: the process answers. Always UP while it runs.</item>
/// <item><c>/api/v1/health/readiness</c>: it can do its job: accepting traffic (not shutting down) and the
/// database answers. This is the probe Railway checks on deploy. Never includes the jobs.</item>
/// <item><c>/api/v1/health</c>: everything, the scheduled jobs included (ADR 0028): DOWN (503) when a job is
/// OVERDUE, WARN (200) when one is FAILING. Cached for 30 seconds, because the URL is public.</item>
/// </list>
/// Status order, as configured in the Java API: DOWN, OUT_OF_SERVICE, WARN, UP, UNKNOWN.
/// </summary>
public static class HealthEndpoints
{
    public const string ActuatorMediaType = "application/vnd.spring-boot.actuator.v3+json";
    private static readonly string[] Order = ["DOWN", "OUT_OF_SERVICE", "WARN", "UP", "UNKNOWN"];

    public static IEndpointRouteBuilder MapZawedHealth(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/health").AllowAnonymous().ExcludeFromDescription();
        group.MapGet("/liveness", (HttpContext context) => Write(context, "UP"));
        group.MapGet("/readiness", async (HttpContext context, NpgsqlDataSource db, ReadinessState readiness) =>
            await Write(context, Aggregate(readiness.AcceptingTraffic ? "UP" : "OUT_OF_SERVICE", await Database(db, context.RequestAborted))));
        group.MapGet("", async (HttpContext context, NpgsqlDataSource db, ReadinessState readiness, JobsHealthCache jobs) =>
            await Write(context, Aggregate("UP", readiness.AcceptingTraffic ? "UP" : "OUT_OF_SERVICE",
                await Database(db, context.RequestAborted), await jobs.StatusAsync(context.RequestAborted))));
        return app;
    }

    /// <summary>The first status in the configured order that any part reports.</summary>
    public static string Aggregate(params string[] statuses) => Order.FirstOrDefault(statuses.Contains) ?? "UNKNOWN";

    private static async Task<string> Database(NpgsqlDataSource db, CancellationToken ct)
    {
        try
        {
            await using var command = db.CreateCommand("select 1");
            command.CommandTimeout = 5;
            await command.ExecuteScalarAsync(ct);
            return "UP";
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            return "DOWN";
        }
    }

    private static async Task Write(HttpContext context, string status)
    {
        context.Response.StatusCode = status is "DOWN" or "OUT_OF_SERVICE" ? 503 : 200;
        context.Response.ContentType = ActuatorMediaType;
        await context.Response.WriteAsync($"{{\"status\":\"{status}\"}}");
    }
}

/// <summary>Spring's readiness state: REFUSING_TRAFFIC (OUT_OF_SERVICE) once shutdown has begun.</summary>
public sealed class ReadinessState
{
    public ReadinessState(IHostApplicationLifetime lifetime) =>
        lifetime.ApplicationStopping.Register(() => AcceptingTraffic = false);

    public bool AcceptingTraffic { get; private set; } = true;
}

/// <summary>The jobs' part of <c>/api/v1/health</c>, cached for 30 seconds (Java <c>JobsHealthIndicator</c>).</summary>
public sealed class JobsHealthCache(IJobsHealth health, TimeProvider clock)
{
    public static readonly TimeSpan Cache = TimeSpan.FromSeconds(30);
    private (string Status, DateTimeOffset At)? cached;

    public async Task<string> StatusAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (cached is { } c && now < c.At + Cache)
        {
            return c.Status;
        }
        string status;
        try
        {
            var statuses = await health.StatusesAsync(ct);
            status = statuses.Any(s => s.State == JobState.OVERDUE) ? "DOWN"
                : statuses.Any(s => s.State == JobState.FAILING) ? "WARN" : "UP";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The database check already reports an unreachable database; this one does not guess.
            status = "UNKNOWN";
        }
        cached = (status, now);
        return status;
    }
}
