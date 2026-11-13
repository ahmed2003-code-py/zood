package com.zawed.api.tenancy;

import java.util.Optional;
import java.util.UUID;

/** What other modules may ask about companies. Reads run in the caller's transaction and scope. */
public interface CompanyDirectory {

	/**
	 * A company as other modules need it: identity, status, the cooldown that applies to its tags
	 * and the balance under which its wallet counts as low (PRD §4.7).
	 */
	record CompanyRef(UUID id, String nameAr, String nameEn, boolean active, int cooldownMinutes,
			long lowBalanceThresholdPiastres) {
	}

	Optional<CompanyRef> find(UUID companyId);

	/** True if the department exists, is active and belongs to the company. */
	boolean hasActiveDepartment(UUID companyId, UUID departmentId);

}
