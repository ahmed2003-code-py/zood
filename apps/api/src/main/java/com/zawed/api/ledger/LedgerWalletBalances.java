package com.zawed.api.ledger;

import java.util.Collection;
import java.util.HashMap;
import java.util.Map;
import java.util.UUID;

import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Component;

@Component
class LedgerWalletBalances implements WalletBalances {

	private final JdbcClient jdbc;

	LedgerWalletBalances(JdbcClient jdbc) {
		this.jdbc = jdbc;
	}

	@Override
	public Map<UUID, Long> balances(Collection<UUID> companyIds) {
		Map<UUID, Long> balances = new HashMap<>();
		if (companyIds.isEmpty()) {
			return balances;
		}
		jdbc.sql("select company_id, balance_piastres from company_wallets where company_id = any(:ids)")
			.param("ids", companyIds.toArray(UUID[]::new))
			.query((rs) -> {
				balances.put(rs.getObject("company_id", UUID.class), rs.getLong("balance_piastres"));
			});
		return balances;
	}

}
