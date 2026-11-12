package com.zawed.api.platform.money;

import java.math.BigInteger;
import java.util.OptionalLong;
import java.util.regex.Pattern;

/**
 * Money and volume arithmetic on integer minor units. No floating point is used for any financial
 * value (PRD §2.3).
 *
 * <ul>
 *   <li>Money is integer piastres (1 EGP = 100 piastres).
 *   <li>Fuel volume is integer millilitres (1 L = 1000 mL).
 *   <li>Rounding is half away from zero, to the nearest unit.
 * </ul>
 *
 * This is the server-side twin of {@code apps/web/src/domain/money.ts}: the same inputs give the
 * same results on both sides, which {@code MoneyMathTests} checks with the web's own test vectors.
 */
public final class MoneyMath {

	public static final long PIASTRES_PER_POUND = 100;
	public static final long ML_PER_LITRE = 1000;

	/**
	 * Largest integer a JavaScript client represents exactly (2^53 - 1). Amounts travel as JSON
	 * numbers, so a value beyond this bound could not be shown or sent back unchanged.
	 */
	public static final long MAX_SAFE = 9_007_199_254_740_991L;

	/**
	 * Exactly what JavaScript's {@code [,\s]} matches in {@code money.ts}: a comma plus ECMAScript
	 * white space (which includes U+FEFF and excludes U+0085, unlike Java's Unicode class).
	 */
	private static final Pattern SEPARATORS = Pattern
		.compile("[,\\t\\n\\x0B\\f\\r \\u00A0\\u1680\\u2000-\\u200A\\u2028\\u2029\\u202F\\u205F\\u3000\\uFEFF]");
	private static final Pattern DECIMAL = Pattern.compile("[0-9]+(\\.[0-9]*)?");

	private MoneyMath() {
	}

	/** Rejects a value that a JavaScript client could not represent exactly. */
	public static long requireSafe(long value, String name) {
		if (value > MAX_SAFE || value < -MAX_SAFE) {
			throw new ArithmeticException(name + " is outside the safe integer range: " + value);
		}
		return value;
	}

	/** Integer division rounded half away from zero. */
	public static long divRoundHalfUp(long numerator, long denominator) {
		if (denominator <= 0) {
			throw new IllegalArgumentException("denominator must be positive");
		}
		long magnitude = Math.absExact(numerator);
		long quotient = Math.addExact(Math.multiplyExact(2L, magnitude), denominator)
				/ Math.multiplyExact(2L, denominator);
		return numerator < 0 ? -quotient : quotient;
	}

	/** Amount in piastres for a dispensed volume at a unit price (piastres per litre). */
	public static long amountForVolume(long volumeMl, long unitPricePiastres) {
		return divRoundHalfUp(Math.multiplyExact(volumeMl, unitPricePiastres), ML_PER_LITRE);
	}

	/** Volume in millilitres that an amount buys at a unit price (piastres per litre). */
	public static long volumeForAmount(long amountPiastres, long unitPricePiastres) {
		return divRoundHalfUp(Math.multiplyExact(amountPiastres, ML_PER_LITRE), unitPricePiastres);
	}

	/**
	 * Parses a user-entered decimal string ("42.5", "1,200.75") into integer minor units at the given
	 * scale (2 for piastres, 3 for millilitres). Empty for invalid input, a negative number, too many
	 * decimals, or a value outside the safe range.
	 */
	public static OptionalLong parseDecimal(String input, int scale) {
		if (input == null || scale < 0) {
			return OptionalLong.empty();
		}
		String normalized = SEPARATORS.matcher(input).replaceAll("");
		if (!DECIMAL.matcher(normalized).matches()) {
			return OptionalLong.empty();
		}
		int point = normalized.indexOf('.');
		String whole = point < 0 ? normalized : normalized.substring(0, point);
		String fraction = point < 0 ? "" : normalized.substring(point + 1);
		if (fraction.length() > scale) {
			return OptionalLong.empty();
		}
		BigInteger minor = new BigInteger(whole + fraction + "0".repeat(scale - fraction.length()));
		if (minor.compareTo(BigInteger.valueOf(MAX_SAFE)) > 0) {
			return OptionalLong.empty();
		}
		return OptionalLong.of(minor.longValueExact());
	}

	public static long poundsToPiastres(long pounds) {
		return Math.multiplyExact(pounds, PIASTRES_PER_POUND);
	}

	public static long litresToMl(long litres) {
		return Math.multiplyExact(litres, ML_PER_LITRE);
	}

}
