package com.zawed.api.identity;

import java.util.UUID;

import jakarta.validation.Valid;

import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.tags.Tag;

import org.springframework.http.HttpStatus;
import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.ResponseStatus;
import org.springframework.web.bind.annotation.RestController;

import com.zawed.api.identity.IdentityApi.AdminUserResponse;
import com.zawed.api.identity.IdentityApi.CreateUserRequest;
import com.zawed.api.identity.IdentityApi.CreatedUserResponse;
import com.zawed.api.identity.IdentityApi.ReasonRequest;
import com.zawed.api.identity.IdentityApi.StatusRequest;
import com.zawed.api.identity.IdentityApi.TemporaryPasswordResponse;
import com.zawed.api.identity.IdentityRecords.UserStatus;
import com.zawed.api.platform.security.Permission;
import com.zawed.api.platform.security.RequiresPermission;
import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiContract;
import com.zawed.api.platform.web.Page;

/** User management (contract §4). Each endpoint names its permission; the service adds Ops' limits. */
@RestController
@RequestMapping(ApiContract.BASE_PATH + "/admin/users")
@Tag(name = "users")
class AdminUsersController {

	private final UserAdmin users;

	AdminUsersController(UserAdmin users) {
		this.users = users;
	}

	@GetMapping
	@RequiresPermission(Permission.USERS_READ)
	@Operation(operationId = "listUsers", summary = "Users, newest first")
	Page<AdminUserResponse> list(@AuthenticationPrincipal ZawedPrincipal principal,
			@RequestParam(required = false) Role role, @RequestParam(required = false) UserStatus status,
			@RequestParam(required = false) String q, @RequestParam(required = false) String cursor,
			@RequestParam(required = false) Integer limit) {
		var page = users.list(principal, role, status, q, cursor, limit);
		return new Page<>(page.items().stream().map(AdminUserResponse::of).toList(), page.nextCursor());
	}

	@PostMapping
	@ResponseStatus(HttpStatus.CREATED)
	@RequiresPermission(Permission.USERS_CREATE)
	@Operation(operationId = "createUser", summary = "Create a user with a temporary password (shown once)")
	CreatedUserResponse create(@AuthenticationPrincipal ZawedPrincipal principal,
			@Valid @RequestBody CreateUserRequest body) {
		UserAdmin.Created created = users.create(principal, body.email(), body.fullName(), body.role(),
				body.scope().toScope());
		return new CreatedUserResponse(AdminUserResponse.of(created.user()), created.temporaryPassword());
	}

	@PostMapping("/{id}/reset-password")
	@RequiresPermission(Permission.USERS_RESET_PASSWORD)
	@Operation(operationId = "resetUserPassword", summary = "Issue a temporary password (shown once)")
	TemporaryPasswordResponse resetPassword(@AuthenticationPrincipal ZawedPrincipal principal, @PathVariable UUID id) {
		return new TemporaryPasswordResponse(users.resetPassword(principal, id));
	}

	@PostMapping("/{id}/unlock")
	@ResponseStatus(HttpStatus.NO_CONTENT)
	@RequiresPermission(Permission.USERS_UNLOCK)
	@Operation(operationId = "unlockUser", summary = "Clear a sign-in lockout")
	void unlock(@AuthenticationPrincipal ZawedPrincipal principal, @PathVariable UUID id) {
		users.unlock(principal, id);
	}

	@PostMapping("/{id}/status")
	@ResponseStatus(HttpStatus.NO_CONTENT)
	@RequiresPermission(Permission.USERS_STATUS)
	@Operation(operationId = "setUserStatus", summary = "Suspend, deactivate or reactivate a user")
	void status(@AuthenticationPrincipal ZawedPrincipal principal, @PathVariable UUID id,
			@Valid @RequestBody StatusRequest body) {
		users.setStatus(principal, id, body.status(), body.reason().trim());
	}

	@PostMapping("/{id}/mfa/reset")
	@ResponseStatus(HttpStatus.NO_CONTENT)
	@RequiresPermission(Permission.USERS_MFA_RESET)
	@Operation(operationId = "resetUserMfa", summary = "Clear a user's second factor; the next sign-in re-enrols")
	void resetMfa(@AuthenticationPrincipal ZawedPrincipal principal, @PathVariable UUID id,
			@Valid @RequestBody ReasonRequest body) {
		users.resetMfa(principal, id, body.reason().trim());
	}

}
