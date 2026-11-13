package com.zawed.api.identity;

import java.util.Locale;
import java.util.Map;

import org.springframework.http.HttpStatus;

import com.zawed.api.platform.web.ApiException;

/**
 * Length is what makes a password strong; composition rules ("one symbol") mostly produce
 * {@code Password1!}. So: 12–128 characters, and not built from the account's own email (contract §6).
 */
final class PasswordPolicy {

	static final int MIN_LENGTH = 12;
	static final int MAX_LENGTH = 128;

	private PasswordPolicy() {
	}

	static void check(String password, String email) {
		int length = password == null ? 0 : password.codePointCount(0, password.length());
		String localPart = email == null ? "" : email.substring(0, Math.max(0, email.indexOf('@'))).toLowerCase(Locale.ROOT);
		boolean containsEmail = localPart.length() >= 3 && password != null
				&& password.toLowerCase(Locale.ROOT).contains(localPart);
		if (length < MIN_LENGTH || length > MAX_LENGTH || containsEmail || (password != null && password.isBlank())) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "PASSWORD_TOO_WEAK", "The password does not meet the policy",
					"newPassword", Map.of("minLength", MIN_LENGTH, "maxLength", MAX_LENGTH));
		}
	}

}
