package com.zawed.api.fueling;

import java.time.Duration;
import java.util.UUID;
import java.util.function.Supplier;

import org.springframework.stereotype.Service;
import org.springframework.transaction.support.TransactionTemplate;

import com.zawed.api.fueling.Fuelings.Fueling;
import com.zawed.api.platform.crypto.LookupHasher;
import com.zawed.api.platform.crypto.SecureTokens;
import com.zawed.api.platform.ratelimit.RateLimiter;
import com.zawed.api.platform.security.DbScope;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.web.ApiContract;
import com.zawed.api.platform.web.ApiException;

/**
 * Receipt links (PRD §6.1: the driver can get the receipt digitally). A link is the fueling id and
 * a keyed signature of it: unguessable, unforgeable without the server key, and naming exactly one
 * receipt. The receipt it opens carries no ids and nothing usable elsewhere.
 */
@Service
class Receipts {

	static final String NAMESPACE = "receipt-link";
	private static final int READS_PER_MINUTE = 30;

	private final Fuelings fuelings;
	private final LookupHasher signer;
	private final RateLimiter limiter;
	private final TransactionTemplate tx;

	Receipts(Fuelings fuelings, LookupHasher signer, RateLimiter limiter, TransactionTemplate tx) {
		this.fuelings = fuelings;
		this.signer = signer;
		this.limiter = limiter;
		this.tx = tx;
	}

	/** The receipt's path under the API base, e.g. {@code /api/v1/receipts/{id}?sig=...}. */
	String pathOf(Fueling fueling) {
		return ApiContract.BASE_PATH + "/receipts/" + fueling.id() + "?sig=" + signature(fueling.id());
	}

	/** The fueling a link names, when its signature is right. Any other link answers 404. */
	Fueling open(UUID id, String sig, String clientAddress) {
		limiter.acquire("receipt:" + SecureTokens.sha256Hex(String.valueOf(clientAddress)), READS_PER_MINUTE,
				Duration.ofMinutes(1));
		if (sig == null || !SecureTokens.constantTimeEquals(signature(id), sig)) {
			throw ApiException.notFound("Receipt");
		}
		// Anonymous by design: the signature is the proof, checked above, and the read names one row.
		return asPlatform(() -> fuelings.find(id)).orElseThrow(() -> ApiException.notFound("Receipt"));
	}

	private String signature(UUID id) {
		return signer.hash(NAMESPACE, id.toString());
	}

	private <T> T asPlatform(Supplier<T> work) {
		return DbScope.runAs(new DbScope.Context(Scope.PLATFORM, null), () -> tx.execute((status) -> work.get()));
	}

}
