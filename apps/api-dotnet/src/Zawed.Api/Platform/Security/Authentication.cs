using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Zawed.Api.Platform.Config;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Platform.Security;

/// <summary>
/// User access tokens (ADR 0008), checked as the Java <c>SecurityConfig</c> checks them: HS256 only (a
/// token naming any other algorithm is refused), issuer <c>zawed-api</c>, a valid time window with 30
/// seconds of skew, claims <c>typ=user</c>, <c>sid</c> and <c>sub</c>. A valid signature is not enough: the
/// session the token names must still be live and its user active (<see cref="ISessionVerifier"/>, read on
/// every request), and the role and scope come from the database, not the token.
/// Deny by default: an endpoint without <c>[AllowAnonymous]</c> needs a signed-in user (the fallback
/// policy), and an unknown route answers 401 to an anonymous caller, so a refusal says nothing about what
/// the API contains.
/// </summary>
public static class Authentication
{
    public const string TokenIssuer = "zawed-api";
    public const string UserTokenType = "user";

    public static IServiceCollection AddZawedAuthentication(this IServiceCollection services)
    {
        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddPosChannel()
            .AddJwtBearer(jwt =>
            {
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = TokenIssuer,
                    ValidateIssuer = true,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                };
                jwt.Events = new JwtBearerEvents
                {
                    // The POS chain never reads a user token (Java posSecurity has no resource server): on
                    // /api/v1/pos/** the bearer is a device token, which only PosChannel checks.
                    OnMessageReceived = context =>
                    {
                        if (PosChannel.IsPosPath(context.Request.Path))
                        {
                            context.NoResult();
                        }
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = async context =>
                    {
                        var token = context.SecurityToken as JsonWebToken;
                        var typ = token?.TryGetPayloadValue<string>("typ", out var t) == true ? t : null;
                        var sid = token?.TryGetPayloadValue<string>("sid", out var s) == true ? s : null;
                        if (typ != UserTokenType || !Guid.TryParse(sid, out var sessionId) || !Guid.TryParse(token?.Subject, out var userId))
                        {
                            context.Fail("Not a user access token");
                            return;
                        }
                        var verifier = context.HttpContext.RequestServices.GetRequiredService<ISessionVerifier>();
                        var principal = await verifier.VerifyAsync(sessionId, userId, context.HttpContext.RequestAborted);
                        if (principal is null)
                        {
                            context.Fail("The session is no longer valid");
                            return;
                        }
                        context.Principal = principal.ToClaimsPrincipal();
                    },
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        await ApiErrors.WriteAsync(context.HttpContext, 401, ErrorCodes.Unauthenticated, "Authentication is required");
                    },
                    OnForbidden = context =>
                        ApiErrors.WriteAsync(context.HttpContext, 403, ErrorCodes.Forbidden, "You do not have permission to do this"),
                };
            });
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<Microsoft.Extensions.Options.IOptions<ZawedOptions>>((jwt, zawed) =>
                jwt.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(zawed.Value.Security.JwtSecretBytes()));
        services.AddAuthorizationBuilder()
            // The device scheme too, so the policy's own authentication keeps a verified device's principal on a POS
            // route (it answers nothing anywhere else, and the user scheme answers nothing on a POS route).
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme, PosChannel.Scheme)
                .RequireAuthenticatedUser().Build());
        return services;
    }

    /// <summary>
    /// An access token that does not verify is refused with 401 even on a public route, as Spring's bearer
    /// filter does: a client with a stale token learns it at once instead of being served as anonymous. POS
    /// routes are exempt: their bearer is a device token, which the POS chain checks.
    /// </summary>
    public static IApplicationBuilder UseInvalidBearerRefusal(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && context.User.Identity?.IsAuthenticated != true
            && !PosChannel.IsPosPath(context.Request.Path))
        {
            // UseAuthentication already ran the default scheme: an unauthenticated user here means the token failed.
            await ApiErrors.WriteAsync(context, 401, ErrorCodes.Unauthenticated, "Authentication is required");
            return;
        }
        await next(context);
    });
}

/// <summary>Until the identity module is ported, no session is valid, so every token is refused.</summary>
internal sealed class NoSessions : ISessionVerifier
{
    public Task<ZawedPrincipal?> VerifyAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<ZawedPrincipal?>(null);
}

/// <summary>
/// A user who must change a temporary password may only read <c>/auth/me</c>, change the password or sign
/// out (Java <c>SecurityFilters.passwordChangeGate</c>, identity contract §4). Paths are compared after
/// <see cref="Web.CanonicalPath"/> has refused every other spelling, so the routed path is the raw path.
/// </summary>
public static class PasswordChangeGate
{
    private static readonly (string Method, string Path)[] Allowed =
    [
        ("GET", "/api/v1/auth/me"), ("POST", "/api/v1/auth/password"), ("POST", "/api/v1/auth/logout"),
    ];

    public static IApplicationBuilder UsePasswordChangeGate(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var principal = ZawedPrincipal.From(context.User);
        if (principal is { MustChangePassword: true })
        {
            var path = context.Request.Path.Value?.TrimEnd('/') ?? "";
            if (!Allowed.Any(a => HttpMethods.Equals(a.Method, context.Request.Method) && string.Equals(a.Path, path, StringComparison.Ordinal)))
            {
                await ApiErrors.WriteAsync(context, 403, "PASSWORD_CHANGE_REQUIRED", "Change the temporary password first");
                return;
            }
        }
        await next(context);
    });
}
