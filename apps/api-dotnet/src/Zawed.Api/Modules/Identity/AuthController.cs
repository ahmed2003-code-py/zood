using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Identity;

/// <summary>
/// Sign-in and the signed-in user's own account (contract §4; Java <c>AuthController</c>). One sign-in path for every
/// role, the Super Admin included, since B7 W10 (ADR 0008 amendment): the role decides which portal opens.
/// <para>The anonymous steps open their own transactions (<see cref="SignIn"/>, <see cref="Sessions"/>), as the Java
/// controller ran outside one; the signed-in ones run on the request's transaction.</para>
/// </summary>
[Route(ApiContract.BasePath + "/auth")]
[Tags("auth")]
public sealed class AuthController(SignIn signIn, Sessions sessions, Account account, AuthCookies cookies, CsrfRule csrf) : ApiControllerBase
{
    [HttpPost("login", Name = "login"), AllowAnonymous, OwnTransactions]
    [EndpointSummary("Sign in with email and password")]
    [ProducesResponseType<LoginResponse>(200)]
    public async Task<LoginResponse> Login(LoginRequest body, CancellationToken ct) =>
        Respond(await signIn.LoginAsync(body.Email, body.Password, HttpContext, ct));

    [HttpPost("mfa/verify", Name = "verifyMfa"), AllowAnonymous, OwnTransactions]
    [EndpointSummary("Second sign-in step: the authenticator code")]
    [ProducesResponseType<LoginResponse>(200)]
    public async Task<LoginResponse> VerifyMfa(ChallengeCodeRequest body, CancellationToken ct) =>
        Respond(await signIn.VerifyMfaAsync(body.ChallengeToken, body.Code, HttpContext, ct));

    [HttpPost("mfa/enroll/start", Name = "startMfaEnrollment"), AllowAnonymous, OwnTransactions]
    [EndpointSummary("The key to add to an authenticator app")]
    [ProducesResponseType<EnrollmentResponse>(200)]
    public async Task<EnrollmentResponse> StartEnrollment(ChallengeRequest body, CancellationToken ct)
    {
        var enrollment = await signIn.StartEnrollmentAsync(body.ChallengeToken, ct);
        return new EnrollmentResponse(enrollment.Secret, enrollment.OtpauthUri);
    }

    [HttpPost("mfa/enroll/confirm", Name = "confirmMfaEnrollment"), AllowAnonymous, OwnTransactions]
    [EndpointSummary("Turn on the second factor with a first code")]
    [ProducesResponseType<LoginResponse>(200)]
    public async Task<LoginResponse> ConfirmEnrollment(ChallengeCodeRequest body, CancellationToken ct) =>
        Respond(await signIn.ConfirmEnrollmentAsync(body.ChallengeToken, body.Code, HttpContext, ct));

    [HttpPost("refresh", Name = "refresh"), AllowAnonymous, OwnTransactions]
    [EndpointSummary("A new access token from the refresh cookie")]
    [ProducesResponseType<SessionResponse>(200)]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        if (!csrf.Accepts(Request))
        {
            return CsrfRejected();
        }
        try
        {
            return Ok(Session(await sessions.RefreshAsync(AuthCookies.Read(Request), ct)));
        }
        catch (ApiException refused)
        {
            // A refused refresh also clears the cookies, so the page stops trying. Answered here rather than by the
            // exception handler, which would drop the Set-Cookie headers with the rest of the response.
            cookies.Clear(Response);
            return new ApiErrorResult(refused.Status, new ResultError(refused.Code, refused.Message, refused.Field, null, refused.Params));
        }
        catch (Exception)
        {
            // Any other failure (the database, a bug) also clears the cookies, as the Java controller does for every
            // RuntimeException. The exception handler clears the response headers before it writes the 500, so the
            // cookies are cleared when the response starts, after that.
            Response.OnStarting(() =>
            {
                cookies.Clear(Response);
                return Task.CompletedTask;
            });
            throw;
        }
    }

    [HttpPost("logout", Name = "logout"), AllowAnonymous, OwnTransactions]
    [EndpointSummary("End the session of the refresh cookie")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (!csrf.Accepts(Request))
        {
            return CsrfRejected();
        }
        await sessions.EndAsync(AuthCookies.Read(Request), ct);
        cookies.Clear(Response);
        return NoContent();
    }

    [HttpGet("me", Name = "me")]
    [EndpointSummary("The signed-in user")]
    [ProducesResponseType<UserResponse>(200)]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var me = await account.MeAsync(Principal, ct);
        return me.IsSuccess ? Ok(UserResponse.Of(me.Data!)) : From(me);
    }

    [HttpPost("password", Name = "changePassword")]
    [EndpointSummary("Change the password; ends the user's other sessions")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest body, CancellationToken ct) =>
        From(await account.ChangePasswordAsync(Principal, body.CurrentPassword, body.NewPassword, ct));

    [HttpGet("sessions", Name = "listSessions")]
    [EndpointSummary("The user's active sessions")]
    [ProducesResponseType<IReadOnlyList<SessionSummaryResponse>>(200)]
    public async Task<IActionResult> ListSessions(CancellationToken ct) => From(await account.SessionsAsync(Principal, ct));

    [HttpDelete("sessions/{id}", Name = "endSession")]
    [EndpointSummary("End one of the user's own sessions")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> EndSession(Guid id, CancellationToken ct) => From(await account.EndSessionAsync(Principal, id, ct));

    private ZawedPrincipal Principal => ZawedPrincipal.From(User)!;

    private IActionResult CsrfRejected() =>
        new ApiErrorResult(403, new ResultError("CSRF_REJECTED", "Request origin not accepted", null, null, null));

    private LoginResponse Respond(SignInOutcome outcome) => outcome switch
    {
        SignInOutcome.Authenticated authenticated => new LoginResponse(LoginStatus.AUTHENTICATED, Session(authenticated.Session), null, null),
        SignInOutcome.MfaRequired challenge => new LoginResponse(LoginStatus.MFA_REQUIRED, null, challenge.ChallengeToken, challenge.ExpiresInSeconds),
        SignInOutcome.EnrollmentRequired challenge =>
            new LoginResponse(LoginStatus.MFA_ENROLLMENT_REQUIRED, null, challenge.ChallengeToken, challenge.ExpiresInSeconds),
        _ => throw new InvalidOperationException("Unknown sign-in outcome"),
    };

    private SessionResponse Session(IssuedSession issued)
    {
        if (issued.RefreshToken is not null)
        {
            cookies.Set(Response, issued.RefreshToken);
        }
        return new SessionResponse(issued.AccessToken, (long)AccessTokens.Lifetime.TotalSeconds, UserResponse.Of(issued.User));
    }
}
