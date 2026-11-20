package com.zawed.api.stationfinance;

import java.util.UUID;

/**
 * Published, synchronously and inside the posting transaction, when a payout has been posted to the
 * ledger: at once when it is at or below the approval threshold, or when a second person approves
 * it. A payout waiting for approval publishes nothing.
 */
public record PayoutPosted(UUID payoutId, UUID operatorId, UUID stationId, long amountPiastres) {
}
