package com.zawed.api.identity;

import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;
import org.springframework.transaction.PlatformTransactionManager;
import org.springframework.transaction.support.TransactionTemplate;

import com.zawed.api.platform.crypto.EncryptedColumn;

@Configuration(proxyBeanMethods = false)
class IdentityConfig {

	/** TOTP secrets, for the data-key rotation job (ADR 0027), bound exactly as {@link SignIn} binds them. */
	@Bean
	EncryptedColumn mfaSecretColumn() {
		return new EncryptedColumn("users", "mfa_secret_encrypted", SignIn::userBinding);
	}

	/** Secrets shown during enrolment and not yet confirmed. */
	@Bean
	EncryptedColumn pendingMfaSecretColumn() {
		return new EncryptedColumn("mfa_challenges", "pending_secret_encrypted", SignIn::challengeBinding);
	}

	/**
	 * Programmatic transactions for the sign-in path, which must open them inside
	 * {@code DbScope.runAs} so the transaction starts with the right scope.
	 */
	@Bean
	TransactionTemplate identityTransactions(PlatformTransactionManager transactionManager) {
		return new TransactionTemplate(transactionManager);
	}

}
