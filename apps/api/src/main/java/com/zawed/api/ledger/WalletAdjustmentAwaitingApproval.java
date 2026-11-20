package com.zawed.api.ledger;

import java.util.UUID;

/**
 * Published, synchronously and inside the transaction, when an Operations user asks for a wallet
 * adjustment (V12, ADR 0021). A different person holding {@code TOPUP_APPROVE} must approve it. A
 * repeat of the same request under its Idempotency-Key answers the existing adjustment and publishes
 * nothing.
 */
public record WalletAdjustmentAwaitingApproval(UUID adjustmentId, UUID companyId, long amountPiastres) {
}
