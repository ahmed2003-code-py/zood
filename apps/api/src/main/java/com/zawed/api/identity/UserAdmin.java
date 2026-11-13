package com.zawed.api.identity;

import java.time.Clock;
import java.time.Instant;
import java.util.Arrays;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import org.springframework.dao.DuplicateKeyException;
import org.springframework.http.HttpStatus;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import com.zawed.api.audit.AuditLog;
import com.zawed.api.identity.IdentityRecords.UserStatus;
import com.zawed.api.identity.IdentityRecords.UserView;
import com.zawed.api.network.OperatorDirectory;
import com.zawed.api.platform.crypto.PasswordHasher;
import com.zawed.api.platform.crypto.SecureTokens;
import com.zawed.api.platform.ids.IdGenerator;
import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.platform.web.Page;
import com.zawed.api.tenancy.CompanyDirectory;

/**
 * User management by Super Admins and, for a narrow set of actions on company and station users,
 * by Ops (contract §4). Every change is audited in its own transaction, with the reason when one
 * is required.
 *
 * <p>Ops never sees platform users: to Ops, a Super Admin or another Ops account does not exist
 * (404), so it cannot reset an administrator's password and take the account over.
 */
@Service
class UserAdmin {

	static final int TEMPORARY_PASSWORD_LENGTH = 16;

	record Created(UserView user, String temporaryPassword) {
	}

	private final IdentityStore store;
	private final PasswordHasher passwords;
	private final AuditLog audit;
	private final IdGenerator ids;
	private final Clock clock;
	private final CompanyDirectory companies;
	private final OperatorDirectory operators;

	UserAdmin(IdentityStore store, PasswordHasher passwords, AuditLog audit, IdGenerator ids, Clock clock,
			CompanyDirectory companies, OperatorDirectory operators) {
		this.store = store;
		this.passwords = passwords;
		this.audit = audit;
		this.ids = ids;
		this.clock = clock;
		this.companies = companies;
		this.operators = operators;
	}

	@Transactional(readOnly = true)
	Page<UserView> list(ZawedPrincipal actor, Role role, UserStatus status, String query, String cursor, Integer limit) {
		List<Role> visible = visibleRoles(actor).stream().filter((r) -> role == null || r == role).toList();
		if (visible.isEmpty()) {
			return new Page<>(List.of(), null);
		}
		int size = Page.limit(limit);
		return Page.of(store.listUsers(visible, status, query, Page.cursor(cursor), size + 1), size, UserView::id);
	}

	@Transactional
	Created create(ZawedPrincipal actor, String rawEmail, String fullName, Role role, Scope scope) {
		if (scope.type() != role.scopeType()) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "SCOPE_MISMATCH",
					"A " + role + " user needs a " + role.scopeType() + " scope", "scope", null);
		}
		boolean scopeExists = switch (scope.type()) {
			case PLATFORM -> true;
			case COMPANY -> companies.find(scope.id()).isPresent();
			case OPERATOR -> operators.find(scope.id()).isPresent();
		};
		if (!scopeExists) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "SCOPE_NOT_FOUND",
					"No " + scope.type().name().toLowerCase() + " with this id", "scope", null);
		}
		String email = SignIn.normalizeEmail(rawEmail);
		if (store.emailTaken(email)) {
			throw emailInUse();
		}
		String temporary = SecureTokens.temporaryPassword(TEMPORARY_PASSWORD_LENGTH);
		UUID id = ids.next();
		Instant now = clock.instant();
		try {
			store.insertUser(id, email, fullName.trim(), passwords.hash(temporary), role, scope, actor.userId(), now);
		}
		catch (DuplicateKeyException ex) {
			throw emailInUse();
		}
		audit.record(actorOf(actor), "USER_CREATED", "USER", id, scope, Map.of("role", role.name()));
		return new Created(store.user(id).orElseThrow(), temporary);
	}

	/** A new temporary password, shown once. Ends every session of the user. */
	@Transactional
	String resetPassword(ZawedPrincipal actor, UUID userId) {
		UserView user = visible(actor, userId);
		String temporary = SecureTokens.temporaryPassword(TEMPORARY_PASSWORD_LENGTH);
		Instant now = clock.instant();
		store.setPassword(user.id(), passwords.hash(temporary), true, actor.userId(), now);
		store.voidChallengesOf(user.id(), now);
		int ended = store.revokeSessionsOf(user.id(), null, "PASSWORD_RESET", now);
		audit.record(actorOf(actor), "PASSWORD_RESET", "USER", user.id(), user.scope(), Map.of("sessionsEnded", ended));
		return temporary;
	}

	@Transactional
	void unlock(ZawedPrincipal actor, UUID userId) {
		UserView user = visible(actor, userId);
		store.unlock(user.id(), actor.userId(), clock.instant());
		audit.record(actorOf(actor), "ACCOUNT_UNLOCKED", "USER", user.id(), user.scope(), Map.of());
	}

	@Transactional
	void setStatus(ZawedPrincipal actor, UUID userId, UserStatus status, String reason) {
		UserView user = visible(actor, userId);
		if (user.id().equals(actor.userId())) {
			throw new ApiException(HttpStatus.CONFLICT, "SELF_CHANGE_NOT_ALLOWED", "You cannot change your own status");
		}
		if (user.role() == Role.SUPER_ADMIN && user.status() == UserStatus.ACTIVE && status != UserStatus.ACTIVE
				&& store.lockActiveSuperAdmins() <= 1) {
			throw new ApiException(HttpStatus.CONFLICT, "LAST_SUPER_ADMIN", "The last active Super Admin must stay active");
		}
		Instant now = clock.instant();
		store.setStatus(user.id(), status, actor.userId(), now);
		if (status != UserStatus.ACTIVE) {
			store.voidChallengesOf(user.id(), now);
		}
		int ended = status == UserStatus.ACTIVE ? 0 : store.revokeSessionsOf(user.id(), null, "STATUS_" + status, now);
		audit.record(actorOf(actor), "USER_STATUS_CHANGED", "USER", user.id(), user.scope(),
				Map.of("from", user.status().name(), "to", status.name(), "reason", reason, "sessionsEnded", ended));
	}

	@Transactional
	void resetMfa(ZawedPrincipal actor, UUID userId, String reason) {
		UserView user = visible(actor, userId);
		Instant now = clock.instant();
		store.resetMfa(user.id(), actor.userId(), now);
		store.voidChallengesOf(user.id(), now);
		int ended = store.revokeSessionsOf(user.id(), null, "MFA_RESET", now);
		audit.record(actorOf(actor), "MFA_RESET", "USER", user.id(), user.scope(),
				Map.of("reason", reason, "sessionsEnded", ended));
	}

	/** The target user, or 404 when the actor may not see it. */
	private UserView visible(ZawedPrincipal actor, UUID userId) {
		return store.user(userId)
			.filter((user) -> visibleRoles(actor).contains(user.role()))
			.orElseThrow(() -> ApiException.notFound("User"));
	}

	private static List<Role> visibleRoles(ZawedPrincipal actor) {
		return switch (actor.role()) {
			case SUPER_ADMIN -> Arrays.asList(Role.values());
			case OPS -> Arrays.stream(Role.values()).filter((role) -> !role.isPlatform()).toList();
			default -> List.of();
		};
	}

	private static AuditLog.Actor actorOf(ZawedPrincipal actor) {
		return new AuditLog.Actor(actor.userId(), actor.role());
	}

	private static ApiException emailInUse() {
		return new ApiException(HttpStatus.CONFLICT, "EMAIL_IN_USE", "This email is already used", "email", null);
	}

}
