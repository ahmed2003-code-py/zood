using System.Net;
using Npgsql;
using Zawed.Api.Platform.Config;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Support;

namespace Zawed.Tests.Platform;

/// <summary>
/// The per-request transaction and scope (Java <c>ScopedTransactionManager</c>, <c>RuntimeRoleGuardTests</c>,
/// <c>RowLevelSecurityTests</c>' platform part), the error envelope (<c>ErrorEnvelopeTests</c>), the JSON
/// rules (<c>WholeNumberTests</c>), and the permission check before the body is read (<c>PermissionFirst</c>).
/// </summary>
public sealed class RequestPipelineTests(ProbeApi api) : IClassFixture<ProbeApi>
{
    private Task<bool> Written(Guid id) => api.Owner.QueryOneAsync<bool>("select exists (select 1 from probe_writes where id = @id)", new { id });

    [Fact]
    public async Task ASuccessCommitsBeforeTheResponseIsWritten()
    {
        var id = Guid.CreateVersion7();
        var response = await api.Client.PostAsync($"/api/v1/test-probe/tx/commit?id={id}", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(await Written(id));
    }

    [Theory]
    [InlineData("refuse", 409, "PROBE_REFUSED")]
    [InlineData("throw", 400, "PROBE_THROWN")]
    [InlineData("invalid", 400, "VALIDATION_FAILED")]
    [InlineData("crash", 500, "INTERNAL_ERROR")]
    public async Task ARefusalOrAFailureRollsBackWhatTheRequestWrote(string outcome, int status, string code)
    {
        var id = Guid.CreateVersion7();
        var body = await Http.AssertErrorAsync(await api.Client.PostAsync($"/api/v1/test-probe/tx/{outcome}?id={id}", null), status, code);
        Assert.False(await Written(id));
        Assert.Equal(status >= 500, body.GetProperty("retryable").GetBoolean());
        Assert.DoesNotContain("secret detail", body.ToString());
    }

    [Fact]
    public async Task AnApiExceptionKeepsItsFieldAndParamsAndAFieldsFailureListsItsFields()
    {
        var thrown = await Http.AssertErrorAsync(await api.Client.PostAsync($"/api/v1/test-probe/tx/throw?id={Guid.CreateVersion7()}", null), 400, "PROBE_THROWN");
        Assert.Equal("id", thrown.GetProperty("field").GetString());
        Assert.Equal(5, thrown.GetProperty("params").GetProperty("max").GetInt32());
        Assert.False(thrown.TryGetProperty("fields", out _));

        var invalid = await Http.AssertErrorAsync(await api.Client.PostAsync($"/api/v1/test-probe/tx/invalid?id={Guid.CreateVersion7()}", null), 400, "VALIDATION_FAILED");
        Assert.Equal("TAKEN", invalid.GetProperty("fields")[0].GetProperty("code").GetString());
        Assert.False(invalid.TryGetProperty("field", out _));
        Assert.False(invalid.TryGetProperty("retryAfterSeconds", out _));
    }

    [Fact]
    public async Task AnAnonymousTransactionRunsAsTheRuntimeRoleAndSeesNoScopedRow()
    {
        var row = await (await api.Client.GetAsync("/api/v1/test-probe/scope")).JsonAsync();
        Assert.Equal("zawed_app", row.GetProperty("role").GetString());
        Assert.Equal("", row.GetProperty("scope_type").GetString());
        Assert.Equal(0, row.GetProperty("visible_users").GetInt64());
    }

    [Fact]
    public async Task ASignedInUsersTransactionCarriesTheirScopeAndRowLevelSecurityShowsOnlyTheirRows()
    {
        var companyA = await Seed.CompanyAsync(api.Owner);
        var companyB = await Seed.CompanyAsync(api.Owner);
        var (userA, _) = await Seed.UserAsync(api.Owner, "FLEET_MANAGER", "COMPANY", companyA);
        await Seed.UserAsync(api.Owner, "ACCOUNTANT", "COMPANY", companyA);
        await Seed.UserAsync(api.Owner, "FLEET_MANAGER", "COMPANY", companyB);
        var principal = new ZawedPrincipal(userA, Guid.CreateVersion7(), Role.FLEET_MANAGER, Scope.Company(companyA), false);

        var row = await (await api.Client.SendAsync(api.As(principal, HttpMethod.Get, "/api/v1/test-probe/scope"))).JsonAsync();

        Assert.Equal("COMPANY", row.GetProperty("scope_type").GetString());
        Assert.Equal(companyA.ToString(), row.GetProperty("scope_id").GetString());
        Assert.Equal(userA.ToString(), row.GetProperty("user_id").GetString());
        Assert.Equal(2, row.GetProperty("visible_users").GetInt64());
    }

    [Fact]
    public async Task APooledConnectionStaysTheRuntimeRoleAndATransactionAsTheOwnerIsRefused()
    {
        var database = new DatabaseOptions
        {
            Host = await TestDatabase.HostAsync(), Port = await TestDatabase.PortAsync(), Name = TestDatabase.SharedName,
            User = TestDatabase.User, Password = TestDatabase.Password, MaxPoolSize = 1,
        };
        await using (var runtime = DataSources.Runtime(database))
        {
            for (var i = 0; i < 20; i++)
            {
                await using var tx = await DbTx.BeginAsync(runtime, DbScopeContext.None, CancellationToken.None);
                Assert.Equal("zawed_app", await tx.QuerySingleAsync<string>("select current_user::text"));
                if (i % 2 == 0)
                {
                    await tx.CommitAsync();
                }
            }
        }
        await using var owner = NpgsqlDataSource.Create(DataSources.OwnerConnectionString(database));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DbTx.BeginAsync(owner, DbScopeContext.None, CancellationToken.None));
        Assert.Contains("row-level security binds only zawed_app", error.Message);
    }

    [Fact]
    public async Task TheScopeIsTransactionLocalSoTheNextTransactionOnTheConnectionStartsClean()
    {
        var database = new DatabaseOptions
        {
            Host = await TestDatabase.HostAsync(), Port = await TestDatabase.PortAsync(), Name = TestDatabase.SharedName,
            User = TestDatabase.User, Password = TestDatabase.Password, MaxPoolSize = 1,
        };
        await using var runtime = DataSources.Runtime(database);
        await using (var first = await DbTx.BeginAsync(runtime, DbScopeContext.Escalate(Scope.Platform, null), CancellationToken.None))
        {
            Assert.Equal("PLATFORM", await first.QuerySingleAsync<string>("select current_setting('app.scope_type', true)"));
            await first.CommitAsync();
        }
        await using var second = await DbTx.BeginAsync(runtime, DbScopeContext.None, CancellationToken.None);
        Assert.Equal("", await second.QuerySingleAsync<string>("select current_setting('app.scope_type', true)"));
    }

    // JSON and validation (Java WholeNumberTests, GlobalExceptionHandler)

    [Theory]
    [InlineData("""{"amountPiastres": 100.5, "name": "x"}""", "amountPiastres")]
    [InlineData("""{"amountPiastres": 100.0, "name": "x"}""", "amountPiastres")]
    [InlineData("""{"amountPiastres": 1e2, "name": "x"}""", "amountPiastres")]
    [InlineData("""{"amountPiastres": 1, "name": "x", "items": [{"volumeMl": 1}, {"volumeMl": 2.5}]}""", "items[1].volumeMl")]
    public async Task AFractionWhereAWholeNumberIsRequiredIsAValidationFailureOnThatField(string json, string field)
    {
        var body = await Http.AssertErrorAsync(await api.Client.SendAsync(Http.Request(HttpMethod.Post, "/api/v1/test-probe/json", rawJson: json)),
            400, "VALIDATION_FAILED");
        Assert.Equal(field, body.GetProperty("fields")[0].GetProperty("field").GetString());
        Assert.Equal("INTEGER_REQUIRED", body.GetProperty("fields")[0].GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("""{"amountPiastres": 1, "name": "x", "unknown": 1}""")]
    [InlineData("""{"amountPiastres": "1", "name": "x"}""")]
    [InlineData("""{"amountPiastres": 1, "name": """)]
    [InlineData("")]
    public async Task AnyOtherUnreadableBodyIsAPlainBadRequestThatEchoesNothing(string json)
    {
        var body = await Http.AssertErrorAsync(await api.Client.SendAsync(Http.Request(HttpMethod.Post, "/api/v1/test-probe/json", rawJson: json)),
            400, "BAD_REQUEST");
        Assert.Equal("Bad Request", body.GetProperty("message").GetString());
        Assert.DoesNotContain("unknown", body.ToString());
    }

    [Fact]
    public async Task ABrokenRuleNamesTheFieldAndTheJakartaCode()
    {
        var body = await Http.AssertErrorAsync(await api.Client.SendAsync(Http.Request(HttpMethod.Post, "/api/v1/test-probe/json",
            rawJson: """{"amountPiastres": 1, "name": " ", "code": "toolong"}""")), 400, "VALIDATION_FAILED");
        var fields = body.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("field").GetString()!, f => f.GetProperty("code").GetString());
        Assert.Equal("NOT_BLANK", fields["name"]);
        Assert.Equal("SIZE", fields["code"]);
    }

    [Fact]
    public async Task AValidBodyRoundTripsInCamelCase()
    {
        var response = await api.Client.SendAsync(Http.Request(HttpMethod.Post, "/api/v1/test-probe/json",
            rawJson: """{"amountPiastres": 9007199254740993, "name": "x", "count": null}"""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"amountPiastres":9007199254740993,"name":"x","code":null,"count":null,"items":null}""", await response.Content.ReadAsStringAsync());
    }

    // Permissions (Java PermissionFirst, AuthorizationMatrixTests' shape)

    [Fact]
    public async Task APermissionIsCheckedBeforeTheBodyIsRead()
    {
        var company = await Seed.CompanyAsync(api.Owner);
        var fleet = new ZawedPrincipal(Guid.CreateVersion7(), Guid.CreateVersion7(), Role.FLEET_MANAGER, Scope.Company(company), false);
        const string invalid = """{"amountPiastres": 1.5, "unknown": true}""";

        await Http.AssertErrorAsync(await api.Client.SendAsync(Http.Request(HttpMethod.Post, "/api/v1/test-probe/permission", rawJson: invalid)),
            401, "UNAUTHENTICATED");
        await Http.AssertErrorAsync(await api.Client.SendAsync(api.As(fleet, HttpMethod.Post, "/api/v1/test-probe/permission", invalid)), 403, "FORBIDDEN");

        var admin = new ZawedPrincipal(Guid.CreateVersion7(), Guid.CreateVersion7(), Role.SUPER_ADMIN, Scope.Platform, false);
        await Http.AssertErrorAsync(await api.Client.SendAsync(api.As(admin, HttpMethod.Post, "/api/v1/test-probe/permission", invalid)), 400, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task APermissionLimitedToAKindOfScopeRefusesTheOtherPortal()
    {
        var station = new ZawedPrincipal(Guid.CreateVersion7(), Guid.CreateVersion7(), Role.STATION_MANAGER, Scope.Operator(Guid.CreateVersion7()), false);
        var fleet = new ZawedPrincipal(Guid.CreateVersion7(), Guid.CreateVersion7(), Role.FLEET_MANAGER, Scope.Company(Guid.CreateVersion7()), false);

        await Http.AssertErrorAsync(await api.Client.SendAsync(api.As(station, HttpMethod.Post, "/api/v1/test-probe/company-tickets")), 403, "FORBIDDEN");
        Assert.Equal(HttpStatusCode.NoContent, (await api.Client.SendAsync(api.As(fleet, HttpMethod.Post, "/api/v1/test-probe/company-tickets"))).StatusCode);
    }

    [Fact]
    public async Task AnAuthenticatedCallerOfAnUnknownRouteGets404AndAnAnonymousOne401()
    {
        var admin = new ZawedPrincipal(Guid.CreateVersion7(), Guid.CreateVersion7(), Role.SUPER_ADMIN, Scope.Platform, false);
        await Http.AssertErrorAsync(await api.Client.SendAsync(api.As(admin, HttpMethod.Get, "/api/v1/no-such-thing")), 404, "NOT_FOUND");
        await Http.AssertErrorAsync(await api.Client.GetAsync("/api/v1/no-such-thing"), 401, "UNAUTHENTICATED");
        await Http.AssertErrorAsync(await api.Client.SendAsync(api.As(admin, HttpMethod.Delete, "/api/v1/meta")), 405, "METHOD_NOT_ALLOWED");
    }

    // Access tokens (Java SecurityConfig.jwtDecoder)

    [Fact]
    public async Task OnlyAValidUserTokenForALiveSessionGetsIn()
    {
        var admin = new ZawedPrincipal(Guid.CreateVersion7(), Guid.CreateVersion7(), Role.SUPER_ADMIN, Scope.Platform, false);
        async Task<HttpStatusCode> With(string token)
        {
            var request = Http.Request(HttpMethod.Get, "/api/v1/test-probe/signed-in");
            request.Headers.Authorization = new("Bearer", token);
            return (await api.Client.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await With(ProbeApi.TokenFor(admin)));
        Assert.Equal(HttpStatusCode.Unauthorized, await With(ProbeApi.TokenFor(admin, typ: "device")));
        Assert.Equal(HttpStatusCode.Unauthorized, await With(ProbeApi.TokenFor(admin, issuer: "someone-else")));
        Assert.Equal(HttpStatusCode.Unauthorized, await With(ProbeApi.TokenFor(admin, secret: new string('x', 48))));
        Assert.Equal(HttpStatusCode.Unauthorized, await With(ProbeApi.TokenFor(admin, expires: DateTime.UtcNow.AddMinutes(-1))));
        var token = ProbeApi.TokenFor(admin);
        ProbeApi.Sessions.TryRemove(admin.SessionId, out _); // signed out: the session row is gone
        Assert.Equal(HttpStatusCode.Unauthorized, await With(token));
        var parts = ProbeApi.TokenFor(admin).Split('.');
        var none = Convert.ToBase64String("""{"alg":"none","typ":"JWT"}"""u8.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(HttpStatusCode.Unauthorized, await With($"{none}.{parts[1]}."));
    }

    [Fact]
    public async Task AUserWhoMustChangeTheirPasswordCanOnlyReachTheAllowedRoutes()
    {
        var user = new ZawedPrincipal(Guid.CreateVersion7(), Guid.CreateVersion7(), Role.OPS, Scope.Platform, true);
        await Http.AssertErrorAsync(await api.Client.SendAsync(api.As(user, HttpMethod.Get, "/api/v1/test-probe/signed-in")), 403, "PASSWORD_CHANGE_REQUIRED");
    }
}
