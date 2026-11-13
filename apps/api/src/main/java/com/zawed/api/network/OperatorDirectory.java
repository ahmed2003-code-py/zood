package com.zawed.api.network;

import java.util.Optional;
import java.util.UUID;

/** What other modules may ask about station operators. Reads run in the caller's transaction and scope. */
public interface OperatorDirectory {

	record OperatorRef(UUID id, String nameAr, String nameEn, boolean active) {
	}

	Optional<OperatorRef> find(UUID operatorId);

	/** True if the station exists (whatever its status). */
	boolean stationExists(UUID stationId);

}
