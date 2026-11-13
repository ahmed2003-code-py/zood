package com.zawed.api.fleet;

import java.text.Normalizer;

/**
 * The comparison form of a plate, used only to find duplicates. The plate itself is stored and shown
 * exactly as registered (approved decision: never transliterated).
 *
 * <p>Egyptian plates mix Arabic letters and digits, written with spaces, dashes or none, in Arabic
 * or Western digits. Two spellings of the same plate must collide, so the key keeps only letters and
 * digits, turns Eastern Arabic and Persian digits into Western ones, and folds the Arabic letter
 * variants people type interchangeably (alef forms, teh marbuta, alef maksura).
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
		return key.toString();
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
