package com.zawed.api.tenancy;

import java.util.UUID;

/**
 * Published, synchronously and inside the onboarding transaction, when a company row has been
 * written. A module that must set something up for every company (the ledger opens its wallet)
 * listens with a plain {@code @EventListener}: its work commits with the company or not at all,
 * and tenancy stays unaware of what that work is.
 */
public record CompanyCreated(UUID companyId) {
}
