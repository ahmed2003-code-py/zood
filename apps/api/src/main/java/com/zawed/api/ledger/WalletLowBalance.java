package com.zawed.api.ledger;

import java.util.UUID;

/**
 * Published, synchronously and inside the posting transaction, when a posting takes a company
 * wallet from at-or-above its low-balance threshold to below it (PRD §4.7). The same fact is written
 * to the audit log as {@code WALLET_LOW_BALANCE}. The alerts module (B4) listens here: with a plain
 * {@code @EventListener} to raise the alert in the same transaction, or through the event outbox for
 * after-commit work such as the e-mail to the Fleet Manager and Accountant.
 */
public record WalletLowBalance(UUID companyId, long balancePiastres, long thresholdPiastres, UUID transactionId) {
}
