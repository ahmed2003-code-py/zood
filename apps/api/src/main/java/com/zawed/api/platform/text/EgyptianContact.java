package com.zawed.api.platform.text;

import java.util.ArrayList;
import java.util.List;
import java.util.function.UnaryOperator;
import java.util.regex.Pattern;

import com.zawed.api.platform.persistence.Sql;
import com.zawed.api.platform.web.ApiError.FieldIssue;
import com.zawed.api.platform.web.FieldsInvalidException;

/**
 * Egyptian contact numbers as people type them, cleaned to one stored form (QA round 1). Cleaning
 * never makes an invalid value valid by guessing: it turns Arabic-Indic and Persian digits into
 * ASCII, removes the separators people type (spaces, dashes, dots, brackets) and the international
 * prefix, and leaves the rest for validation ({@link #PHONE}, {@link #MOBILE}, {@link #TAX_ID},
 * {@link #COMMERCIAL_REGISTRATION}) to accept or refuse. Request records call these in their compact
 * constructors, so bean validation judges the cleaned value and the service stores it.
 */
public final class EgyptianContact {

	/** Separators people type inside a number. */
	private static final Pattern SEPARATORS = Pattern.compile("[\\s\\p{Z}\\-.()\\u2010-\\u2015]+");

	/** A normalised Egyptian mobile number: 010, 011, 012 or 015, then eight digits. */
	public static final String MOBILE = "^01[0125][0-9]{8}$";

	/**
	 * A normalised Egyptian mobile or landline. Landlines: Cairo and Giza {@code 02} and eight digits,
	 * Alexandria {@code 03} and seven, every other area code ({@code 013}, {@code 015}, {@code 040}
	 * to {@code 097}) three digits and seven.
	 */
	public static final String PHONE = "^(01[0125][0-9]{8}|02[0-9]{8}|03[0-9]{7}|01[35][0-9]{7}|0[4-9][0-9]{8})$";

	/** The Egyptian tax registration number, stored as nine digits (typed {@code xxx-xxx-xxx} or not). */
	public static final String TAX_ID = "^[0-9]{9}$";

	/**
	 * A commercial registration number, stored as its digits. Registries number per office, so only
	 * the shape is checked: 3 to 12 digits.
	 */
	public static final String COMMERCIAL_REGISTRATION = "^[0-9]{3,12}$";

	private EgyptianContact() {
	}

	/**
	 * A phone number as {@code 0…}: {@code +20}, {@code 0020}, or {@code 20} before a twelve-digit
	 * mobile become the leading {@code 0}, and a ten-digit mobile without its zero gets it back.
	 * Anything else is returned without separators, for validation to refuse. Null stays null.
	 */
	public static String phone(String value) {
		if (value == null) {
			return null;
		}
		String compact = compact(value);
		if (compact.startsWith("+20")) {
			compact = "0" + compact.substring(3);
		}
		else if (compact.startsWith("0020")) {
			compact = "0" + compact.substring(4);
		}
		else if (compact.length() == 12 && compact.startsWith("201")) {
			compact = "0" + compact.substring(2);
		}
		else if (compact.length() == 10 && compact.startsWith("1")) {
			compact = "0" + compact;
		}
		return compact;
	}

	/** A tax registration number without its separators. Null stays null. */
	public static String taxId(String value) {
		return value == null ? null : compact(value);
	}

	/** A commercial registration without its separators. Null stays null. */
	public static String commercialRegistration(String value) {
		return value == null ? null : compact(value);
	}

	/** For an optional field: nothing typed is no value. */
	public static String orNull(String value) {
		return value == null || value.isBlank() ? null : value;
	}

	/**
	 * Cleans and judges the contact numbers of one request, against the stored row when there is one
	 * (QA round 1 review). A value sent back exactly as stored is kept as it is, even one written before
	 * these rules: a full-body edit of an old row must not fail on a field nobody touched. Every other
	 * value is cleaned and must match; the failures are collected and thrown together.
	 */
	public static final class Check {

		private final String prefix;
		private final List<FieldIssue> issues = new ArrayList<>();

		/** @param prefix put before each field name, such as {@code company.} for a nested body */
		public Check(String prefix) {
			this.prefix = prefix;
		}

		public String phone(String field, String typed, String stored) {
			return judge(field, typed, stored, EgyptianContact::phone, PHONE);
		}

		public String mobile(String field, String typed, String stored) {
			return judge(field, typed, stored, EgyptianContact::phone, MOBILE);
		}

		public String taxId(String field, String typed, String stored) {
			return judge(field, typed, stored, EgyptianContact::taxId, TAX_ID);
		}

		public String commercialRegistration(String field, String typed, String stored) {
			return judge(field, typed, stored, EgyptianContact::commercialRegistration, COMMERCIAL_REGISTRATION);
		}

		/** Throws 400 VALIDATION_FAILED naming every field that failed, if any did. */
		public void orThrow() {
			if (!issues.isEmpty()) {
				throw new FieldsInvalidException(issues);
			}
		}

		private String judge(String field, String typed, String stored, UnaryOperator<String> cleaner, String rule) {
			String asTyped = Sql.clean(typed);
			if (asTyped == null) {
				return null;
			}
			if (asTyped.equals(stored)) {
				return stored;
			}
			String cleaned = orNull(cleaner.apply(asTyped));
			if (cleaned == null) {
				return null;
			}
			if (!cleaned.matches(rule)) {
				issues.add(new FieldIssue(prefix + field, "PATTERN", "must match \"" + rule + "\""));
				return null;
			}
			return cleaned;
		}

	}

	private static String compact(String value) {
		StringBuilder digits = new StringBuilder(value.length());
		value.codePoints().forEach((c) -> {
			if (c >= '٠' && c <= '٩') {
				digits.append((char) ('0' + (c - '٠')));
			}
			else if (c >= '۰' && c <= '۹') {
				digits.append((char) ('0' + (c - '۰')));
			}
			else {
				digits.appendCodePoint(c);
			}
		});
		return SEPARATORS.matcher(digits).replaceAll("");
	}

}
