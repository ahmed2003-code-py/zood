package com.zawed.api.ledger;

import java.util.UUID;

/**
 * Published, synchronously and inside the transaction, when a top-up reaches {@code PENDING_APPROVAL}:
 * an Operations user verified a company's transfer, or recorded one the company never announced
 * (ADR 0021). A second person holding {@code TOPUP_APPROVE} must now approve it. Not published again
 * when a recorded transfer is sent a second time and answers the existing record.
 *
 * @param amountPiastres the verified amount, the one that will be credited
 */
public record TopUpAwaitingApproval(UUID topUpId, UUID companyId, long amountPiastres) {
}
