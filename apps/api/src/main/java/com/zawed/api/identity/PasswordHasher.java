package com.zawed.api.identity;

import org.springframework.security.crypto.argon2.Argon2PasswordEncoder;
import org.springframework.stereotype.Component;

/**
 * Argon2id with OWASP's recommended minimum: 19 MiB memory, 2 iterations, 1 lane, 16-byte salt,
 * 32-byte hash. Around 50–100 ms per check on a server core: negligible for one sign-in, ruinous for
 * an attacker with a stolen table.
 *
 * <p>A check against an unknown or locked account still runs a full Argon2 verification against a
 * dummy hash, so response time does not reveal whether an email is registered.
 */
@Component
class PasswordHasher {

	private static final int SALT_BYTES = 16;
	private static final int HASH_BYTES = 32;
	private static final int PARALLELISM = 1;
	private static final int MEMORY_KIB = 19 * 1024;
	private static final int ITERATIONS = 2;

	private final Argon2PasswordEncoder encoder = new Argon2PasswordEncoder(SALT_BYTES, HASH_BYTES, PARALLELISM,
			MEMORY_KIB, ITERATIONS);

	/** A real hash of a random value nobody knows, computed once. */
	private final String dummyHash = encoder.encode(com.zawed.api.platform.crypto.SecureTokens.random(32));

	String hash(String password) {
		return encoder.encode(password);
	}

	boolean matches(String password, String hash) {
		return encoder.matches(password, hash);
	}

	/** Spends the same work as a real check and always fails. */
	void burn(String password) {
		encoder.matches(password, dummyHash);
	}

}
