package com.zawed.api.platform.security;

import java.util.Objects;
import java.util.UUID;

/** A data scope. A platform scope has no id; a company or operator scope always has one. */
public record Scope(ScopeType type, UUID id) {

	public static final Scope PLATFORM = new Scope(ScopeType.PLATFORM, null);

	public Scope {
		Objects.requireNonNull(type, "type");
		if ((type == ScopeType.PLATFORM) != (id == null)) {
			throw new IllegalArgumentException("A " + type + " scope " + (id == null ? "needs" : "has no") + " id");
		}
	}

	public static Scope company(UUID id) {
		return new Scope(ScopeType.COMPANY, id);
	}

	public static Scope operator(UUID id) {
		return new Scope(ScopeType.OPERATOR, id);
	}

	public boolean isPlatform() {
		return type == ScopeType.PLATFORM;
	}

}
