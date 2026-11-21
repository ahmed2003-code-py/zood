using System.Net;
using System.Text.RegularExpressions;
using Zawed.Tests.Support;

namespace Zawed.Tests.Platform;

/// <summary>
/// Port of the Java <c>ApiSmokeTests</c> (phase B0's promise: the service starts, reaches its database and
/// refuses everything else), plus the response headers and CORS answers the Java API gives.
/// </summary>
public sealed class ApiSmokeTests(SharedApi api) : IClassFixture<SharedApi>
{
    private HttpClient Client => api.Client;

    [Fact]
    public async Task HealthAnswersWithoutDetails()
    {
        var response = await Client.GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"status":"UP"}""", await response.Content.ReadAsStringAsync());
        Assert.Equal("application/vnd.spring-boot.actuator.v3+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task LivenessAndReadinessAreSeparateProbes()
    {
        Assert.Equal("""{"status":"UP"}""", await (await Client.GetAsync("/api/v1/health/liveness")).Content.ReadAsStringAsync());
        var readiness = await Client.GetAsync("/api/v1/health/readiness");
        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
        Assert.Equal("""{"status":"UP"}""", await readiness.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MetaReportsTheServiceAndTheAppliedSchema()
    {
        var response = await Client.GetAsync("/api/v1/meta");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal(["service", "version", "contractVersion", "environment", "serverTime", "schemaVersion", "posContract", "posContractMinimum"],
            body.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("zawed-api", body.GetProperty("service").GetString());
        Assert.EndsWith("-dotnet", body.GetProperty("version").GetString());
        Assert.Equal(1, body.GetProperty("contractVersion").GetInt32());
        Assert.Equal("test", body.GetProperty("environment").GetString());
        Assert.Equal("23", body.GetProperty("schemaVersion").GetString());
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?Z$", body.GetProperty("serverTime").GetString());
        Assert.Equal(1, body.GetProperty("posContract").GetInt32());
        Assert.Equal(1, body.GetProperty("posContractMinimum").GetInt32());
    }

    [Fact]
    public async Task EverythingNotListedAsPublicIsRefusedInTheErrorEnvelope()
    {
        var response = await Client.GetAsync("/api/v1/companies");

        var body = await Http.AssertErrorAsync(response, 401, "UNAUTHENTICATED");
        Assert.False(body.GetProperty("retryable").GetBoolean());
        Assert.Equal("Authentication is required", body.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("/api/v1/no-such-thing")]
    [InlineData("/actuator/env")]
    [InlineData("/")]
    [InlineData("/api/v1/health/db")]
    [InlineData("/api/v1/health/anything")]
    [InlineData("/api/v1/env")]
    [InlineData("/api/v1/heapdump")]
    [InlineData("/api/v1")]
    public async Task ARouteThatDoesNotExistIsRefusedTheSameWay(string path) =>
        await Http.AssertErrorAsync(await Client.GetAsync(path), 401, "UNAUTHENTICATED");

    [Fact]
    public async Task APublicRouteIsPublicForReadingOnly() =>
        await Http.AssertErrorAsync(await Client.PostAsync("/api/v1/meta", null), 401, "UNAUTHENTICATED");

    [Fact]
    public async Task AnAccessTokenThatDoesNotVerifyIsRefusedEvenOnAPublicRoute()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/meta");
        request.Headers.Authorization = new("Bearer", "not-a-token");

        await Http.AssertErrorAsync(await Client.SendAsync(request), 401, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task EchoesAPlainInboundRequestId()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/meta");
        request.Headers.Add("X-Request-Id", "web-7f3a.42_b");

        Assert.Equal("web-7f3a.42_b", (await Client.SendAsync(request)).Header("X-Request-Id"));
    }

    [Fact]
    public async Task ReplacesAnInboundRequestIdThatIsNotPlain()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/meta");
        request.Headers.TryAddWithoutValidation("X-Request-Id", "bad id; with=separators");

        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", (await Client.SendAsync(request)).Header("X-Request-Id"));
    }

    [Fact]
    public async Task AnswersAPreflightFromAnAllowedOrigin()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/meta");
        request.Headers.Add("Origin", "http://localhost:5173");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization, x-zawed-client");

        var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("http://localhost:5173", response.Header("Access-Control-Allow-Origin"));
        Assert.Equal("true", response.Header("Access-Control-Allow-Credentials"));
        Assert.Equal("3600", response.Header("Access-Control-Max-Age"));
    }

    [Fact]
    public async Task RefusesAPreflightFromAnyOtherOrigin()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/meta");
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Header("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task RefusesAnActualRequestFromAnotherOriginBeforeItIsServed()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/meta");
        request.Headers.Add("Origin", "https://evil.example");

        Assert.Equal(HttpStatusCode.Forbidden, (await Client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task ExposesTheRequestIdToAnAllowedOrigin()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/meta");
        request.Headers.Add("Origin", "http://localhost:5173");

        var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("X-Request-Id", response.Header("Access-Control-Expose-Headers"));
    }

    [Fact]
    public async Task SendsTheSecurityHeadersSpringSecuritySends()
    {
        var response = await Client.GetAsync("/api/v1/meta");

        var cache = response.Headers.CacheControl!; // HttpClient re-orders the directives it parses
        Assert.True(cache.NoCache && cache.NoStore && cache.MustRevalidate && cache.MaxAge == TimeSpan.Zero);
        Assert.Equal("no-cache", response.Header("Pragma"));
        Assert.Equal("0", response.Header("Expires"));
        Assert.Equal("nosniff", response.Header("X-Content-Type-Options"));
        Assert.Equal("DENY", response.Header("X-Frame-Options"));
        Assert.Equal("0", response.Header("X-XSS-Protection"));
        Assert.Contains("Origin", response.Header("Vary"));
    }

    [Fact]
    public async Task ServesTheOpenApiDocumentAndTheReference()
    {
        var document = await Client.GetAsync("/api/v1/openapi.json");
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
        Assert.Equal("ZAWED API", (await document.JsonAsync()).GetProperty("info").GetProperty("title").GetString());

        var docs = await Client.GetAsync("/api/v1/docs/index.html");
        Assert.Equal(HttpStatusCode.OK, docs.StatusCode);
        Assert.Contains("swagger", await docs.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }
}
