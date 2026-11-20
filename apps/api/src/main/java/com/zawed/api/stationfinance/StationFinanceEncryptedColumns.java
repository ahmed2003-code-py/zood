package com.zawed.api.stationfinance;

import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;

import com.zawed.api.platform.crypto.EncryptedColumn;

/** Station finance's encrypted columns, for the data-key rotation job (ADR 0027). */
@Configuration(proxyBeanMethods = false)
class StationFinanceEncryptedColumns {

	/** Bank account numbers, bound exactly as {@link BankAccounts} binds them. */
	@Bean
	EncryptedColumn bankAccountColumn() {
		return new EncryptedColumn("station_bank_accounts", "account_encrypted", BankAccounts::binding);
	}

}
