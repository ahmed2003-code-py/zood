package com.zawed.api.stationfinance;

import java.util.UUID;

/**
 * Published, synchronously and inside the transaction, when a station's bank account change is
 * approved or rejected. The account number, its last digits and the reason are not part of it.
 */
public record BankAccountChangeDecided(UUID changeId, UUID operatorId, UUID stationId, boolean approved) {
}
