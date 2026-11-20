package com.zawed.api.fleet;

import java.text.Normalizer;

/**
 * The comparison form of a plate, used only to find duplicates. The plate itself is stored and shown
 * exactly as registered (approved decision: never transliterated).
 *
 * <p>Egyptian plates mix Arabic letters and digits, written with spaces, dashes or none, in Arabic
 * or Western digits. Two spellings of the same plate must collide, so the key keeps only letters and
 * digits, turns Eastern Arabic and Persian digits into Western ones, and folds the Arabic letter
 * variants people type interchangeably (alef forms, teh marbuta, alef maksura). Since V14 the digit
 * group comes first for plates of the Egyptian shape (see {@link #canonical}); the database applies
 * the same rule to existing keys ({@code plate_key_canonical}).
 */
final class PlateKey {

	private PlateKey() {
	}

	static String of(String plate) {
		String normalized = Normalizer.normalize(plate, Normalizer.Form.NFKC);
		StringBuilder key = new StringBuilder(normalized.length());
		normalized.codePoints().forEach((cp) -> {
			int c = fold(cp);
			if (Character.isLetterOrDigit(c)) {
				key.appendCodePoint(Character.toUpperCase(c));
			}
		});
		return canonical(key.toString());
	}

	/** U+0640, the Arabic tatweel: it stretches a letter and carries no meaning of its own. */
	static final String TATWEEL = "\u0640";

	/**
	 * What a search compares (QA round 1): the key without tatweels, on the query and on the stored
	 * key alike ({@code Vehicles.list}). Identity is not changed: {@link #of} still keeps a tatweel, so
	 * the keys of stored plates and the unique rule on them stay exactly as V14 left them.
	 */
	static String searchKey(String query) {
		return canonical(of(query).replace(TATWEEL, ""));
	}

	/**
	 * The Egyptian shape (one digit group and one letter group) in one order, digits first, so the
	 * order a plate was typed in does not matter (V14). Any other shape stays as it is: two different
	 * plates must never share a key.
	 */
	static String canonical(String key) {
		if (key.matches("[0-9]+[^0-9]+") || key.matches("[^0-9]+[0-9]+")) {
			return key.replaceAll("[^0-9]", "") + key.replaceAll("[0-9]", "");
		}
		return key;
	}

	private static int fold(int cp) {
		if (cp >= 0x0660 && cp <= 0x0669) { // Arabic-Indic digits
			return '0' + (cp - 0x0660);
		}
		if (cp >= 0x06F0 && cp <= 0x06F9) { // Extended Arabic-Indic (Persian) digits
			return '0' + (cp - 0x06F0);
		}
		return switch (cp) {
			case 0x0623, 0x0625, 0x0622, 0x0671 -> 0x0627; // أ إ آ ٱ -> ا
			case 0x0629 -> 0x0647; // ة -> ه
			case 0x0649 -> 0x064A; // ى -> ي
			default -> cp;
		};
	}

}
