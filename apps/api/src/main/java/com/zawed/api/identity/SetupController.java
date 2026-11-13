package com.zawed.api.identity;

import jakarta.servlet.http.HttpServletRequest;
import jakarta.validation.Valid;

import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.tags.Tag;

import org.springframework.http.HttpStatus;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.ResponseStatus;
import org.springframework.web.bind.annotation.RestController;

import com.zawed.api.identity.IdentityApi.SetupRequest;
import com.zawed.api.identity.IdentityApi.UserResponse;
import com.zawed.api.platform.web.ApiContract;

/**
 * Creates the first Super Admin, once. Works only while {@code ZAWED_BOOTSTRAP_TOKEN} is set and no
 * Super Admin exists; afterwards it answers 404 like a route that does not exist.
 */
@RestController
@RequestMapping(ApiContract.BASE_PATH + "/setup")
@Tag(name = "auth")
class SetupController {

	private final SignIn signIn;

	SetupController(SignIn signIn) {
		this.signIn = signIn;
	}

	@PostMapping("/super-admin")
	@ResponseStatus(HttpStatus.CREATED)
	@Operation(operationId = "bootstrapSuperAdmin", summary = "Create the first Super Admin (one time)")
	UserResponse createSuperAdmin(@Valid @RequestBody SetupRequest body, HttpServletRequest request) {
		return UserResponse.of(signIn.bootstrap(body.setupToken(), body.email(), body.fullName(), body.password(), request));
	}

}
