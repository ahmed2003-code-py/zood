package com.zawed.api.ledger;

import java.util.UUID;

/**
 * Published, synchronously and inside the posting transaction, when an approved top-up has credited
 * the company wallet. Exactly once per top-up: a repeated approval posts nothing and publishes
 * nothing.
 *
 * @param amountPiastres what the wallet received (the verified amount)
 */
public record TopUpCredited(UUID topUpId, UUID companyId, long amountPiastres, UUID transactionId) {
}
