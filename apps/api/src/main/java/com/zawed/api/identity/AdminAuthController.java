package com.zawed.api.identity;

import jakarta.servlet.http.HttpServletRequest;
import jakarta.servlet.http.HttpServletResponse;
import jakarta.validation.Valid;

import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.tags.Tag;

import org.springframework.http.HttpStatus;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.ResponseStatus;
import org.springframework.web.bind.annotation.RestController;

import com.zawed.api.identity.IdentityApi.LoginRequest;
import com.zawed.api.identity.IdentityApi.LoginResponse;
import com.zawed.api.identity.IdentityApi.SessionResponse;
import com.zawed.api.identity.IdentityRecords.Door;
import com.zawed.api.platform.security.CsrfRule;
import com.zawed.api.platform.web.ApiContract;

/**
 * The Super Admin's separate sign-in door (PRD §6.4): its own path and its own refresh cookie.
 * Super Admin credentials are refused at {@code /auth/login}, and every other role is refused here.
 */
@RestController
@RequestMapping(ApiContract.BASE_PATH + "/admin/auth")
@Tag(name = "auth")
class AdminAuthController {

	private final SignIn signIn;
	private final Sessions sessions;
	private final AuthCookies cookies;
	private final CsrfRule csrf;

	AdminAuthController(SignIn signIn, Sessions sessions, AuthCookies cookies, CsrfRule csrf) {
		this.signIn = signIn;
		this.sessions = sessions;
		this.cookies = cookies;
		this.csrf = csrf;
	}

	@PostMapping("/login")
	@Operation(operationId = "adminLogin", summary = "Super Admin sign-in")
	LoginResponse login(@Valid @RequestBody LoginRequest body, HttpServletRequest request, HttpServletResponse response) {
		return AuthController.respond(signIn.login(Door.ADMIN, body.email(), body.password(), request), cookies, response);
	}

	@PostMapping("/refresh")
	@Operation(operationId = "adminRefresh", summary = "A new access token from the admin refresh cookie")
	SessionResponse refresh(HttpServletRequest request, HttpServletResponse response) {
		return AuthController.refresh(Door.ADMIN, request, response, sessions, cookies, csrf);
	}

	@PostMapping("/logout")
	@ResponseStatus(HttpStatus.NO_CONTENT)
	@Operation(operationId = "adminLogout", summary = "End the admin session")
	void logout(HttpServletRequest request, HttpServletResponse response) {
		AuthController.logout(Door.ADMIN, request, response, sessions, cookies, csrf);
	}

}
