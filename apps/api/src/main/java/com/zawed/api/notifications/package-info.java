/**
 * In-app notifications (B7 work stream W5; owner decision 2026-11-19: in-app only, no e-mail yet).
 *
 * <p>No module depends on this one. Producers publish their own domain events (ledger
 * {@code WalletLowBalance}, {@code TopUpAwaitingApproval}, {@code TopUpCredited}, {@code TopUpRejected},
 * {@code WalletAdjustmentAwaitingApproval}; fueling {@code HardAlertRaised}; tickets {@code TicketOpened},
 * {@code TicketAnswered}; stationfinance {@code PayoutPosted}, {@code BankAccountChangeDecided}), and
 * {@code NotificationTriggers} turns each into a notification inside the
 * publishing transaction, in a savepoint: a notification exists only if its cause committed, and one
 * that cannot be written is rolled back, counted ({@code zawed.notifications.dropped}) and logged
 * while its cause still commits.
 *
 * <p>A notification is addressed to an audience (a scope and roles in it), carries ids, integers and
 * codes only (the web writes the text from its translations), and is read through
 * {@code /api/v1/notifications} by whoever holds one of those roles in that scope. Contract:
 * {@code docs/architecture/b7-notifications-contract.md}; schema: {@code V19__notifications.sql}.
 */
package com.zawed.api.notifications;
