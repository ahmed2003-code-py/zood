package com.zawed.api.identity;

import java.util.Collection;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.UUID;

import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ScopeType;
import com.zawed.api.platform.security.ZawedPrincipal;

/**
 * Names for the user ids that maker-checker records carry (top-ups, wallet adjustments, bank
 * account changes, payouts), so a card reads "Verified by Hana" instead of an id (QA round 1).
 *
 * <p>What a caller may see, enforced twice:
 * <ul>
 * <li>A platform caller (Super Admin, Ops Manager, Ops) sees every name and role.</li>
 * <li>A company or station caller sees the names and roles of the users of its own scope. Any other
 * actor is a platform user and is shown as {@link Actors.Role#ZAWED_OPERATIONS} with no name, so
 * no staff name or e-mail reaches a tenant.</li>
 * </ul>
 * The read runs in the caller's scope, so row-level security on {@code users} (V3) already hides
 * every user outside it; the code checks the scope of each row again and never returns an e-mail.
 */
@Service
public class ActorDirectory {

	private final JdbcClient jdbc;

	ActorDirectory(JdbcClient jdbc) {
		this.jdbc = jdbc;
	}

	private record Row(UUID id, String fullName, String role, ScopeType scopeType, UUID scopeId) {
	}

	/** How each of these users is shown to {@code viewer}. Nulls are ignored. */
	@Transactional(readOnly = true)
	public Actors describe(ZawedPrincipal viewer, Collection<UUID> userIds) {
		List<UUID> wanted = userIds.stream().filter(Objects::nonNull).distinct().toList();
		if (wanted.isEmpty()) {
			return Actors.NONE;
		}
		Map<UUID, Row> rows = new HashMap<>();
		jdbc.sql("select id, full_name, role, scope_type, scope_id from users where id in (:ids)")
			.param("ids", wanted)
			.query((rs, n) -> new Row(rs.getObject("id", UUID.class), rs.getString("full_name"), rs.getString("role"),
					ScopeType.valueOf(rs.getString("scope_type")), rs.getObject("scope_id", UUID.class)))
			.list()
			.forEach((row) -> rows.put(row.id(), row));
		boolean platform = viewer.scope().type() == ScopeType.PLATFORM;
		Map<UUID, Actors.Actor> actors = new HashMap<>();
		for (UUID id : wanted) {
			Row row = rows.get(id);
			if (platform) {
				actors.put(id, row == null ? new Actors.Actor(null, null)
						: new Actors.Actor(row.fullName(), Actors.Role.valueOf(row.role())));
			}
			else if (row != null && sameScope(viewer.scope(), row)) {
				actors.put(id, new Actors.Actor(row.fullName(), Actors.Role.valueOf(row.role())));
			}
			else {
				actors.put(id, new Actors.Actor(null, Actors.Role.ZAWED_OPERATIONS));
			}
		}
		return new Actors(actors);
	}

	private static boolean sameScope(Scope scope, Row row) {
		return row.scopeType() == scope.type() && Objects.equals(row.scopeId(), scope.id());
	}

}
