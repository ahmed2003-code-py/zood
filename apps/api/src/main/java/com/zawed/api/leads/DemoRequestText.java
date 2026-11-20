package com.zawed.api.leads;

import java.util.Locale;
import java.util.regex.Pattern;

/**
 * How the demo form's text is cleaned before it is validated (u1-leads-contract.md §2). Cleaning
 * never makes an invalid value valid by guessing: it removes what a person does not mean (spaces at
 * the ends, runs of spaces, line breaks pasted into a name, the formatting of a phone number) and
 * leaves the rest for validation to accept or refuse.
 */
final class DemoRequestText {

	/** Every kind of whitespace, Unicode included (no-break and wide spaces, line and paragraph separators). */
	private static final Pattern WHITESPACE = Pattern.compile("[\\s\\p{Z}\\u0085]+");

	/**
	 * What validation refuses in a name: every "other" character (Unicode category C): control
	 * characters that are not whitespace, format characters (zero-width spaces and joiners, the left-
	 * and right-to-left and Arabic letter marks, the bidirectional embeddings, overrides and isolates,
	 * word joiners, the byte-order mark, soft hyphens, tags), private-use, unassigned and lone surrogate
	 * code points. They are invisible, or make a name read differently in the Ops console than what was
	 * stored. The one exception is the zero-width non-joiner (U+200C), which shapes Arabic-script text.
	 */
	// A character-class union, not an alternation: a repeated group with "|" makes Java's regex engine
	// recurse once per character, and a long value would overflow the stack (a 500, and a cheap DoS).
	static final String PLAIN_TEXT = "^[\\u200C[^\\p{C}]]*$";

	/** The same for an Ops note, which keeps its line breaks and tabs. */
	static final String NOTE_TEXT = "^[\\t\\n\\r\\u200C[^\\p{C}]]*$";

	/**
	 * An e-mail address: one {@code @}, a dot in the domain, and no whitespace (Unicode spaces such as
	 * U+00A0 and U+3000 included) or category-C character anywhere.
	 */
	static final String EMAIL = "[^@\\s\\p{Z}\\p{C}]+@[^@\\s\\p{Z}\\p{C}]+\\.[^@\\s\\p{Z}\\p{C}]+";

	/** Separators people type inside a phone number. */
	private static final Pattern PHONE_SEPARATORS = Pattern.compile("[\\s\\p{Z}\\-.()\\u2010-\\u2015]+");

	/** A normalised Egyptian mobile number: 010, 011, 012 or 015, then eight digits. */
	static final String EGYPTIAN_MOBILE = "^01[0125][0-9]{8}$";

	private DemoRequestText() {
	}

	/** Trims and collapses every run of whitespace (tabs and line breaks too) to one space; null stays null. */
	static String collapse(String value) {
		if (value == null) {
			return null;
		}
		return WHITESPACE.matcher(value).replaceAll(" ").strip();
	}

	/**
	 * An Egyptian mobile number as {@code 01XXXXXXXXX}: Arabic-Indic and Persian digits become ASCII,
	 * spaces, dashes, dots and brackets go, and {@code +20}, {@code 0020} or a bare {@code 20} before
	 * the number, or a missing leading zero, become the leading {@code 0}. Anything else is returned
	 * as typed (without separators) for validation to refuse.
	 */
	static String phone(String value) {
		if (value == null) {
			return null;
		}
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
		String compact = PHONE_SEPARATORS.matcher(digits).replaceAll("");
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

	/** Trimmed and lower-cased, so a repeat with other capitals is the same contact. */
	static String email(String value) {
		if (value == null) {
			return null;
		}
		return value.strip().toLowerCase(Locale.ROOT);
	}

	/** A note keeps its line breaks; only the ends are trimmed. Blank is no note. */
	static String note(String value) {
		if (value == null) {
			return null;
		}
		String trimmed = value.strip();
		return trimmed.isEmpty() ? null : trimmed;
	}

}
