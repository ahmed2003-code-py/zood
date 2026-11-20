package com.zawed.api.identity;

import java.util.Map;
import java.util.UUID;

/**
 * How the people who acted on a record are shown to the signed-in caller (QA round 1), from
 * {@link ActorDirectory#describe}. A response record asks it for a name and a role per user id; an
 * id that is null (nobody acted yet) has neither.
 */
public final class Actors {

	/** The role shown beside a name. A tenant sees ZAWED's staff only as {@link #ZAWED_OPERATIONS}. */
	public enum Role {

		SUPER_ADMIN, OPS_MANAGER, OPS, FLEET_MANAGER, ACCOUNTANT, STATION_MANAGER,
		/** A platform user, as a company or a station reads them: the team, not the person. */
		ZAWED_OPERATIONS

	}

	/** One person as the caller may see them: the name is null when the caller may not read it. */
	public record Actor(String name, Role role) {
	}

	static final Actors NONE = new Actors(Map.of());

	private final Map<UUID, Actor> byId;

	Actors(Map<UUID, Actor> byId) {
		this.byId = Map.copyOf(byId);
	}

	/** The display name, or null: nobody acted, the caller may not read it, or the user is unknown. */
	public String name(UUID userId) {
		Actor actor = userId == null ? null : byId.get(userId);
		return actor == null ? null : actor.name();
	}

	/** The role label, or null when nobody acted. */
	public Role role(UUID userId) {
		Actor actor = userId == null ? null : byId.get(userId);
		return actor == null ? null : actor.role();
	}

}
