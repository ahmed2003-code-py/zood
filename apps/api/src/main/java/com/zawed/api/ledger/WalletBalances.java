package com.zawed.api.ledger;

import java.util.Collection;
import java.util.Map;
import java.util.UUID;

/**
 * Cached wallet balances for reading, without locking (the POS snapshot, B5 contract §2). Never use
 * these to decide a posting: decide under {@link Ledger#lockWallet}. Runs in the caller's scope.
 */
public interface WalletBalances {

	/** Balance in piastres for each of these companies that has a wallet visible to the caller. */
	Map<UUID, Long> balances(Collection<UUID> companyIds);

}
