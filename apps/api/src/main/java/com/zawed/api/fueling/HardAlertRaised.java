package com.zawed.api.fueling;

import java.util.UUID;

/**
 * Published, synchronously and inside the transaction that raised it, when a hard alert (PRD §10)
 * is written. Not published when the database already holds the same alert (a rejected sync item
 * sent again, V8). Null ids do not apply to the alert.
 *
 * @param type the alert type's name, such as {@code LOW_BALANCE} or {@code SYNC_CONFLICT}
 * @param severity {@code WARNING} or {@code CRITICAL}
 */
public record HardAlertRaised(UUID alertId, String type, String severity, UUID companyId, UUID operatorId,
		UUID stationId, UUID fuelingId) {
}
