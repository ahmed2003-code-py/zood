package com.zawed.api.ledger;

import java.text.Normalizer;

import org.springframework.http.HttpStatus;

import com.zawed.api.platform.persistence.Sql;
import com.zawed.api.platform.web.ApiException;

/**
 * Bank transfer references, as people type and paste them. One transfer must be one transfer
 * however its reference was written: a top-up credits once (B3) and a payout pays once (B6), each
 * guarded by a unique index on the {@link #key} of its reference.
 */
public final class BankReferences {

	private BankReferences() {
	}

	/**
	 * The comparison form of a bank reference: one transfer must have one key however it was typed
	 * or pasted. NFKC (full-width and compatibility forms), digits of any script folded to 0-9
	 * ({@code ١٢٣} is {@code 123}), upper case, and then only A-Z and 0-9 kept: spaces, dashes,
	 * non-breaking spaces and invisible direction marks copied from Arabic interfaces all vanish.
	 * {@code " ft-١٢٣ "}, {@code "FT 123"} and {@code "FT‏123"} are the same transfer.
	 * The database checks the same shape (V6_1, V9).
	 */
	public static String key(String bankReference) {
		String normalized = Normalizer.normalize(bankReference, Normalizer.Form.NFKC);
		StringBuilder key = new StringBuilder(normalized.length());
		normalized.codePoints().forEach((cp) -> {
			if (Character.isDigit(cp)) {
				key.append((char) ('0' + Character.digit(cp, 10)));
				return;
			}
			int upper = Character.toUpperCase(cp);
			if (upper >= 'A' && upper <= 'Z') {
				key.append((char) upper);
			}
		});
		return key.toString();
	}

	/**
	 * The reference as it is stored for display: trimmed, inner whitespace collapsed.
	 *
	 * @throws ApiException 400 {@code INVALID_BANK_REFERENCE} when nothing is left to compare
	 */
	public static String clean(String bankReference) {
		String reference = Sql.clean(bankReference);
		if (reference == null || key(reference).isEmpty()) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "INVALID_BANK_REFERENCE", "A bank reference is required",
					"bankReference", null);
		}
		return reference;
	}

}
