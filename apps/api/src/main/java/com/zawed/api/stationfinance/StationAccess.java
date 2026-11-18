package com.zawed.api.stationfinance;

import java.util.UUID;

import org.springframework.stereotype.Component;

import com.zawed.api.network.StationDirectory;
import com.zawed.api.network.StationDirectory.StationRef;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ScopeType;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiException;

/**
 * The API half of tenant isolation for station finance (plan §3.6): the scope comes from the
 * signed-in user, never from the request. Row-level security is the other half and would hide the
 * rows anyway; this keeps the answer a plain 404 instead of an empty statement.
 */
@Component
class StationAccess {

	private final StationDirectory stations;

	StationAccess(StationDirectory stations) {
		this.stations = stations;
	}

	/**
	 * The station, if the caller may read its finances: any station for Operations, the operator's
	 * own stations for a station manager. Must run inside the caller's transaction.
	 *
	 * @throws ApiException 404 for a station that does not exist and for one outside the caller's
	 * scope alike
	 */
	StationRef require(ZawedPrincipal principal, UUID stationId) {
		StationRef station = stations.findStation(stationId).orElseThrow(() -> ApiException.notFound("Station"));
		Scope scope = principal.scope();
		boolean allowed = scope.isPlatform()
				|| (scope.type() == ScopeType.OPERATOR && station.operatorId().equals(scope.id()));
		if (!allowed) {
			throw ApiException.notFound("Station");
		}
		return station;
	}

	/** The operator a list is limited to: the caller's own for a station manager, the asked one for Operations. */
	static UUID operatorFilter(ZawedPrincipal principal, UUID requestedOperatorId) {
		return principal.scope().type() == ScopeType.OPERATOR ? principal.scope().id() : requestedOperatorId;
	}

}
