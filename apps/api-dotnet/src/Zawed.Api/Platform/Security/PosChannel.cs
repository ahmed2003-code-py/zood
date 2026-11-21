using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Platform.Security;

/// <summary>
/// Identifies the device, and the agent signed in on it, behind a request to <c>/api/v1/pos/**</c> (B4 contract §1;
/// Java <c>PosAuthenticator</c>). Implemented by the network module, which owns devices and agents; the platform only
/// calls it, so platform code does not depend on a business module.
/// <para>The principal returned carries the <see cref="PosChannel.AuthorityClaim"/> <see cref="PosChannel.Device"/>, plus
/// <see cref="PosChannel.Agent"/> for an agent who may work or <see cref="PosChannel.AgentPinChange"/> for one who must
/// first replace the PIN Ops gave them.</para>
/// </summary>
public interface IPosAuthenticator
{
    /// <summary>The device and agent behind the request.</summary>
    /// <exception cref="ApiException">401 when the device token, or an agent session the request names, is missing,
    /// invalid or no longer live; 426 for a terminal below the minimum POS contract; 429 over the per-device limit.</exception>
    Task<ClaimsPrincipal> AuthenticateAsync(HttpContext context);
}

/// <summary>
/// The POS terminal's chain (Java <c>SecurityConfig.posSecurity</c>, <c>SecurityFilters.posAuthentication</c> and
/// <c>SecurityErrorHandler.posAccessDenied</c>). Devices hold no user token: a request to <c>/api/v1/pos/**</c> is
/// identified by a device token (and an agent session), never by a user's access token, so a user token is refused on
/// POS routes (the user scheme does not even read it there, <see cref="Authentication"/>) and a device token everywhere
/// else (it fails the user scheme's signature check).
/// <list type="bullet">
/// <item><c>POST /pos/activate</c> and <c>POST /pos/token</c> obtain credentials and check their own proof: no device.</item>
/// <item>Every other POS request must carry a live device token, and passes the rules of <see cref="Refusal"/>: the
/// device alone for agent sign-in, the snapshot and sync (B5); an agent, or one who must change the PIN, for the PIN
/// change and sign-out; a working agent for anything else, unknown routes included.</item>
/// <item>A refused device answers 401 <c>DEVICE_UNAUTHENTICATED</c> (or the authenticator's own refusal); a device
/// without an agent 401 <c>AGENT_SESSION_REQUIRED</c>; an agent who must change the PIN 403 <c>PIN_CHANGE_REQUIRED</c>.</item>
/// </list>
/// Paths are compared ordinally on the decoded path, after <see cref="CanonicalPath"/> has refused every other spelling
/// (Java matched with Spring's own case-sensitive matcher, without trailing-slash matching).
/// </summary>
public static class PosChannel
{
    public const string Scheme = "Pos";
    public const string PathPrefix = "/api/v1/pos";
    public const string AuthorityClaim = "zawed:pos-authority";
    public const string Device = "POS_DEVICE";
    public const string Agent = "POS_AGENT";
    public const string AgentPinChange = "POS_AGENT_PIN_CHANGE";

    /// <summary>The two POS calls that obtain credentials; each checks its own proof (B4 contract §1).</summary>
    public static readonly IReadOnlySet<string> PublicPost = new HashSet<string>(StringComparer.Ordinal)
    {
        "/api/v1/pos/activate", "/api/v1/pos/token",
    };

    private static readonly object RefusalKey = new();

    /// <summary>A path of the POS chain: <c>/api/v1/pos</c> and below, case-sensitive as Spring's matcher.</summary>
    public static bool IsPosPath(PathString path) => path.StartsWithSegments(PathPrefix, StringComparison.Ordinal);

    /// <summary>One of the two credential-obtaining calls, by method and exact path.</summary>
    public static bool IsPublic(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) && PublicPost.Contains(request.Path.Value ?? "");

    public static bool Has(ClaimsPrincipal? user, string authority) => user?.HasClaim(AuthorityClaim, authority) == true;

    /// <summary>
    /// The chain's authorisation rules, in Java's order, for an authenticated device; null when the request may go on.
    /// </summary>
    public static ApiException? Refusal(HttpRequest request, ClaimsPrincipal user)
    {
        var path = request.Path.Value ?? "";
        var post = HttpMethods.IsPost(request.Method);
        bool allowed;
        if (post && path is "/api/v1/pos/agents/login" or "/api/v1/pos/sync"
            || HttpMethods.IsGet(request.Method) && path == "/api/v1/pos/snapshot")
        {
            allowed = Has(user, Device);
        }
        else if (post && path is "/api/v1/pos/agents/pin" or "/api/v1/pos/agents/logout")
        {
            allowed = Has(user, Agent) || Has(user, AgentPinChange);
        }
        else
        {
            allowed = Has(user, Agent);
        }
        if (allowed)
        {
            return null;
        }
        if (Has(user, AgentPinChange))
        {
            return new ApiException(403, "PIN_CHANGE_REQUIRED", "Change the PIN first");
        }
        return Has(user, Device) && !Has(user, Agent)
            ? new ApiException(401, "AGENT_SESSION_REQUIRED", "An agent must sign in")
            : new ApiException(403, ErrorCodes.Forbidden, "You do not have permission to do this");
    }

    public static AuthenticationBuilder AddPosChannel(this AuthenticationBuilder authentication) =>
        authentication.AddScheme<AuthenticationSchemeOptions, PosAuthenticationHandler>(Scheme, null);

    /// <summary>
    /// The chain itself, right after the user scheme ran: on a POS path, authenticate the device (its refusal ends the
    /// request here, so a controller never runs for an unverified device) and apply the rules.
    /// </summary>
    public static IApplicationBuilder UsePosChannel(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (!IsPosPath(context.Request.Path) || IsPublic(context.Request))
        {
            await next(context);
            return;
        }
        var result = await context.AuthenticateAsync(Scheme);
        if (!result.Succeeded)
        {
            await WriteAsync(context, context.Items[RefusalKey] as ApiException
                ?? new ApiException(401, "DEVICE_UNAUTHENTICATED", "The device could not be authenticated"));
            return;
        }
        context.User = result.Principal!;
        if (Refusal(context.Request, context.User) is { } refusal)
        {
            await WriteAsync(context, refusal);
            return;
        }
        await next(context);
    });

    private static Task WriteAsync(HttpContext context, ApiException refusal) => refusal is RateLimitedException limited
        ? ApiErrors.WriteAsync(context, 429, limited.Code, limited.Message, @params: limited.Params,
            retryAfterSeconds: (int)Math.Min(int.MaxValue, limited.RetryAfterSeconds), retryable: true)
        : ApiErrors.WriteAsync(context, refusal.Status, refusal.Code, refusal.Message, refusal.Field, @params: refusal.Params);

    /// <summary>
    /// The device scheme. It answers only on a non-public POS path, and keeps the authenticator's refusal for
    /// <see cref="UsePosChannel"/> to write. The result is computed once per request (the framework memoises it), so the
    /// authorisation middleware's second look reads the same principal without touching the database again. It never
    /// challenges or forbids: the chain has already written every POS refusal, and the user scheme answers elsewhere.
    /// </summary>
    internal sealed class PosAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!IsPosPath(Request.Path) || IsPublic(Request))
            {
                return AuthenticateResult.NoResult();
            }
            var authenticator = Context.RequestServices.GetService<IPosAuthenticator>();
            if (authenticator is null)
            {
                return AuthenticateResult.Fail("No device channel");
            }
            try
            {
                return AuthenticateResult.Success(new AuthenticationTicket(await authenticator.AuthenticateAsync(Context), Scheme.Name));
            }
            catch (ApiException refused)
            {
                Context.Items[RefusalKey] = refused;
                return AuthenticateResult.Fail(refused.Code);
            }
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties) => Task.CompletedTask;

        protected override Task HandleForbiddenAsync(AuthenticationProperties properties) => Task.CompletedTask;
    }
}
