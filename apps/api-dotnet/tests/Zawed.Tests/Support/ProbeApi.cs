using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Tests.Support;

/// <summary>
/// The application plus test-only endpoints (<see cref="ProbeController"/>) that exercise the platform: the
/// per-request transaction, the scope, the JSON rules, validation and the permission filter. Sessions are
/// whatever the test registers in <see cref="Sessions"/>, so a token can be minted for any principal.
/// </summary>
public sealed class ProbeApi : IAsyncLifetime
{
    public const string JwtSecret = "local-only-jwt-secret-do-not-deploy-0123456789abcdef";

    public ZawedApiFactory Factory { get; } = new ProbeFactory();

    public static ConcurrentDictionary<Guid, ZawedPrincipal> Sessions { get; } = new();

    public HttpClient Client { get; private set; } = null!;

    public Owner Owner => Factory.Owner;

    public async Task InitializeAsync()
    {
        Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        await Owner.ExecuteAsync("""
            create table if not exists probe_writes (id uuid primary key, note text not null);
            grant select, insert on probe_writes to zawed_app;
            """);
    }

    public async Task DisposeAsync() => await Factory.DisposeAsync();

    /// <summary>A signed access token for <paramref name="principal"/>, whose session the probe verifier knows.</summary>
    public static string TokenFor(ZawedPrincipal principal, string typ = "user", string issuer = "zawed-api", string? secret = null,
        DateTime? expires = null)
    {
        Sessions[principal.SessionId] = principal;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Expires = expires ?? DateTime.UtcNow.AddMinutes(15),
            NotBefore = (expires ?? DateTime.UtcNow.AddMinutes(15)).AddMinutes(-16),
            IssuedAt = (expires ?? DateTime.UtcNow.AddMinutes(15)).AddMinutes(-15),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = principal.UserId.ToString(), ["sid"] = principal.SessionId.ToString(), ["typ"] = typ,
            },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret ?? JwtSecret)),
                SecurityAlgorithms.HmacSha256),
        });
    }

    public HttpRequestMessage As(ZawedPrincipal principal, HttpMethod method, string path, string? json = null)
    {
        var request = Http.Request(method, path, rawJson: json);
        request.Headers.Authorization = new("Bearer", TokenFor(principal));
        return request;
    }

    private sealed class ProbeFactory : ZawedApiFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddControllers().AddApplicationPart(typeof(ProbeController).Assembly);
                services.Replace(ServiceDescriptor.Scoped<ISessionVerifier, ProbeSessions>());
            });
        }
    }

    private sealed class ProbeSessions : ISessionVerifier
    {
        public Task<ZawedPrincipal?> VerifyAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(Sessions.TryGetValue(sessionId, out var p) && p.UserId == userId ? p : null);
    }
}

public sealed record ProbeBody(
    long AmountPiastres,
    [NotBlank] string? Name,
    [Size(max: 5)] string? Code,
    int? Count,
    IReadOnlyList<ProbeItem>? Items);

public sealed record ProbeItem(long VolumeMl);

/// <summary>Test-only endpoints. Never part of the application assembly.</summary>
[Route("api/v1/test-probe")]
public sealed class ProbeController(IDbSession db) : ApiControllerBase
{
    [HttpPost("tx/{outcome}"), AllowAnonymous]
    public async Task<IActionResult> Write(string outcome, [FromQuery] Guid id)
    {
        await db.Current.ExecuteAsync("insert into probe_writes (id, note) values (@id, @outcome)", new { id, outcome });
        return outcome switch
        {
            "commit" => From(Result<object>.Created(new { id })),
            "refuse" => From(Result<object>.Conflict("PROBE_REFUSED", "Refused after writing")),
            "throw" => throw new ApiException(400, "PROBE_THROWN", "Thrown after writing", "id", new Dictionary<string, object> { ["max"] = 5 }),
            "invalid" => throw new FieldsInvalidException([new FieldIssue("id", "TAKEN", "already used")]),
            _ => throw new InvalidOperationException("secret detail that must not leak"),
        };
    }

    [HttpGet("scope"), AllowAnonymous]
    public async Task<object> Scope() => await db.Current.QuerySingleAsync<ScopeRow>(
        "select current_setting('app.scope_type', true) as scope_type, current_setting('app.scope_id', true) as scope_id, "
        + "current_setting('app.user_id', true) as user_id, current_user::text as role, (select count(*) from users) as visible_users");

    public sealed record ScopeRow(string? scope_type, string? scope_id, string? user_id, string role, long visible_users);

    [HttpPost("json"), AllowAnonymous]
    public ProbeBody Json(ProbeBody body) => body;

    [HttpPost("permission"), RequiresPermission(Api.Platform.Security.Permission.USERS_CREATE)]
    public ProbeBody NeedsPermission(ProbeBody body) => body;

    [HttpPost("company-tickets"), RequiresPermission(Api.Platform.Security.Permission.TICKETS_OPEN, Scopes = [ScopeType.COMPANY])]
    public IActionResult CompanyTickets() => NoContent();

    [HttpGet("signed-in")]
    public object SignedIn() => new { ok = true };
}
