package com.zawed.api.platform.security;

import java.util.UUID;
import java.util.function.Supplier;

import org.springframework.security.core.Authentication;
import org.springframework.security.core.context.SecurityContextHolder;

/**
 * The data scope a database transaction runs in. Row-level security reads it (V3 policies), so it
 * decides which rows a query can see, whatever the query says.
 *
 * <p>Normally it is the signed-in user's scope, taken from the security context. {@link #runAs} is
 * used in exactly two places, each after its own proof is checked: the sign-in path, which runs its
 * writes as the user it has just identified, and the POS channel (network.DeviceChannel,
 * network.AgentSessions, the fueling engine), which has no user and runs as the platform once the
 * device and agent are verified (B4 contract §1). {@code ArchitectureTests} keeps that list closed.
 */
public final class DbScope {

	/** What the transaction is told: the scope, and the acting user if there is one. */
	public record Context(Scope scope, UUID userId) {
	}

	private static final ThreadLocal<Context> OVERRIDE = new ThreadLocal<>();

	private DbScope() {
	}

	/** The scope for a transaction starting now, or null when nobody is identified (no rows visible). */
	public static Context current() {
		Context override = OVERRIDE.get();
		if (override != null) {
			return override;
		}
		Authentication authentication = SecurityContextHolder.getContext().getAuthentication();
		if (authentication != null && authentication.getPrincipal() instanceof ZawedPrincipal principal) {
			return new Context(principal.scope(), principal.userId());
		}
		return null;
	}

	/**
	 * Runs {@code work} with an explicit scope. Transactions must start inside {@code work}; one that
	 * is already open keeps the scope it started with.
	 */
	public static <T> T runAs(Context context, Supplier<T> work) {
		Context previous = OVERRIDE.get();
		OVERRIDE.set(context);
		try {
			return work.get();
		}
		finally {
			if (previous == null) {
				OVERRIDE.remove();
			}
			else {
				OVERRIDE.set(previous);
			}
		}
	}

}
