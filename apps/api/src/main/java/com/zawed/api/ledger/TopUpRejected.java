package com.zawed.api.ledger;

import java.util.UUID;

/**
 * Published, synchronously and inside the transaction, when Operations rejects a top-up, at either
 * step. The reason is free text and stays in the top-up row and the audit log.
 *
 * @param companyRequested true when the company asked for it, false for a transfer Operations
 *        recorded itself
 * @param amountPiastres the amount the company claimed (or Operations recorded)
 */
public record TopUpRejected(UUID topUpId, UUID companyId, boolean companyRequested, long amountPiastres) {
}
