package com.zawed.api.identity;

import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;
import org.springframework.transaction.PlatformTransactionManager;
import org.springframework.transaction.support.TransactionTemplate;

@Configuration(proxyBeanMethods = false)
class IdentityConfig {

	/**
	 * Programmatic transactions for the sign-in path, which must open them inside
	 * {@code DbScope.runAs} so the transaction starts with the right scope.
	 */
	@Bean
	TransactionTemplate identityTransactions(PlatformTransactionManager transactionManager) {
		return new TransactionTemplate(transactionManager);
	}

}
