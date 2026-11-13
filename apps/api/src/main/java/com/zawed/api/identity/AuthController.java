package com.zawed.api.identity;

import java.util.List;
import java.util.UUID;

import jakarta.servlet.http.HttpServletRequest;
import jakarta.servlet.http.HttpServletResponse;
import jakarta.validation.Valid;

import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.tags.Tag;

import org.springframework.http.HttpStatus;
import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.web.bind.annotation.DeleteMapping;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.ResponseStatus;
import org.springframework.web.bind.annotation.RestController;

import com.zawed.api.identity.IdentityApi.ChallengeCodeRequest;
import com.zawed.api.identity.IdentityApi.ChallengeRequest;
import com.zawed.api.identity.IdentityApi.ChangePasswordRequest;
import com.zawed.api.identity.IdentityApi.EnrollmentResponse;
import com.zawed.api.identity.IdentityApi.LoginRequest;
import com.zawed.api.identity.IdentityApi.LoginResponse;
import com.zawed.api.identity.IdentityApi.LoginStatus;
import com.zawed.api.identity.IdentityApi.SessionResponse;
import com.zawed.api.identity.IdentityApi.SessionSummaryResponse;
import com.zawed.api.identity.IdentityApi.UserResponse;
import com.zawed.api.identity.IdentityRecords.Door;
import com.zawed.api.platform.security.CsrfRule;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiContract;

/** Sign-in and the signed-in user's own account (contract §4). */
@RestController
@RequestMapping(ApiContract.BASE_PATH + "/auth")
@Tag(name = "auth")
class AuthController {

	private final SignIn signIn;
	private final Sessions sessions;
	private final Account account;
	private final AuthCookies cookies;
	private final CsrfRule csrf;

	AuthController(SignIn signIn, Sessions sessions, Account account, AuthCookies cookies, CsrfRule csrf) {
		this.signIn = signIn;
		this.sessions = sessions;
		this.account = account;
		this.cookies = cookies;
		this.csrf = csrf;
	}

	@PostMapping("/login")
	@Operation(operationId = "login", summary = "Sign in with email and password")
	LoginResponse login(@Valid @RequestBody LoginRequest body, HttpServletRequest request, HttpServletResponse response) {
		return respond(signIn.login(Door.USER, body.email(), body.password(), request), cookies, response);
	}

	@PostMapping("/mfa/verify")
	@Operation(operationId = "verifyMfa", summary = "Second sign-in step: the authenticator code")
	LoginResponse verifyMfa(@Valid @RequestBody ChallengeCodeRequest body, HttpServletRequest request,
			HttpServletResponse response) {
		return respond(signIn.verifyMfa(body.challengeToken(), body.code(), request), cookies, response);
	}

	@PostMapping("/mfa/enroll/start")
	@Operation(operationId = "startMfaEnrollment", summary = "The key to add to an authenticator app")
	EnrollmentResponse startEnrollment(@Valid @RequestBody ChallengeRequest body) {
		SignIn.Enrollment enrollment = signIn.startEnrollment(body.challengeToken());
		return new EnrollmentResponse(enrollment.secret(), enrollment.otpauthUri());
	}

	@PostMapping("/mfa/enroll/confirm")
	@Operation(operationId = "confirmMfaEnrollment", summary = "Turn on the second factor with a first code")
	LoginResponse confirmEnrollment(@Valid @RequestBody ChallengeCodeRequest body, HttpServletRequest request,
			HttpServletResponse response) {
		return respond(signIn.confirmEnrollment(body.challengeToken(), body.code(), request), cookies, response);
	}

	@PostMapping("/refresh")
	@Operation(operationId = "refresh", summary = "A new access token from the refresh cookie")
	SessionResponse refresh(HttpServletRequest request, HttpServletResponse response) {
		return refresh(Door.USER, request, response, sessions, cookies, csrf);
	}

	@PostMapping("/logout")
	@ResponseStatus(HttpStatus.NO_CONTENT)
	@Operation(operationId = "logout", summary = "End the session of the refresh cookie")
	void logout(HttpServletRequest request, HttpServletResponse response) {
		logout(Door.USER, request, response, sessions, cookies, csrf);
	}

	@GetMapping("/me")
	@Operation(operationId = "me", summary = "The signed-in user")
	UserResponse me(@AuthenticationPrincipal ZawedPrincipal principal) {
		return UserResponse.of(account.me(principal));
	}

	@PostMapping("/password")
	@ResponseStatus(HttpStatus.NO_CONTENT)
	@Operation(operationId = "changePassword", summary = "Change the password; ends the user's other sessions")
	void changePassword(@AuthenticationPrincipal ZawedPrincipal principal,
			@Valid @RequestBody ChangePasswordRequest body) {
		account.changePassword(principal, body.currentPassword(), body.newPassword());
	}

	@GetMapping("/sessions")
	@Operation(operationId = "listSessions", summary = "The user's active sessions")
	List<SessionSummaryResponse> sessions(@AuthenticationPrincipal ZawedPrincipal principal) {
		return account.sessions(principal)
			.stream()
			.map((session) -> new SessionSummaryResponse(session.id(), session.id().equals(principal.sessionId()),
					session.createdAt(), session.lastSeenAt(), session.userAgent(), session.ipPrefix()))
			.toList();
	}

	@DeleteMapping("/sessions/{id}")
	@ResponseStatus(HttpStatus.NO_CONTENT)
	@Operation(operationId = "endSession", summary = "End one of the user's own sessions")
	void endSession(@AuthenticationPrincipal ZawedPrincipal principal, @PathVariable UUID id) {
		account.endSession(principal, id);
	}

	// Shared with the admin door.

	static LoginResponse respond(SignIn.Outcome outcome, AuthCookies cookies, HttpServletResponse response) {
		return switch (outcome) {
			case SignIn.Outcome.Authenticated authenticated -> new LoginResponse(LoginStatus.AUTHENTICATED,
					session(authenticated.session(), cookies, response), null, null);
			case SignIn.Outcome.MfaRequired challenge -> new LoginResponse(LoginStatus.MFA_REQUIRED, null,
					challenge.challengeToken(), challenge.expiresInSeconds());
			case SignIn.Outcome.EnrollmentRequired challenge -> new LoginResponse(LoginStatus.MFA_ENROLLMENT_REQUIRED,
					null, challenge.challengeToken(), challenge.expiresInSeconds());
		};
	}

	static SessionResponse refresh(Door door, HttpServletRequest request, HttpServletResponse response,
			Sessions sessions, AuthCookies cookies, CsrfRule csrf) {
		csrf.check(request);
		try {
			return session(sessions.refresh(cookies.read(request, door), door), cookies, response);
		}
		catch (RuntimeException ex) {
			// A refused refresh also clears the cookies, so the page stops trying.
			cookies.clear(response, door);
			throw ex;
		}
	}

	static void logout(Door door, HttpServletRequest request, HttpServletResponse response, Sessions sessions,
			AuthCookies cookies, CsrfRule csrf) {
		csrf.check(request);
		sessions.end(cookies.read(request, door));
		cookies.clear(response, door);
	}

	private static SessionResponse session(Sessions.Issued issued, AuthCookies cookies, HttpServletResponse response) {
		if (issued.refreshToken() != null) {
			cookies.set(response, issued.door(), issued.refreshToken());
		}
		return new SessionResponse(issued.accessToken(), AccessTokens.LIFETIME.toSeconds(), UserResponse.of(issued.user()));
	}

}
