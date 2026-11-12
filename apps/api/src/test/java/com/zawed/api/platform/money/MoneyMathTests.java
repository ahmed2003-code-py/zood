package com.zawed.api.platform.money;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import org.junit.jupiter.api.Nested;
import org.junit.jupiter.api.Test;

/**
 * The same vectors as {@code apps/web/src/domain/money.test.ts}. A change here that is not also
 * made there (or the reverse) means the POS and the server would disagree on an amount.
 */
class MoneyMathTests {

	@Nested
	class DivRoundHalfUp {

		@Test
		void roundsHalfAwayFromZero() {
			assertThat(MoneyMath.divRoundHalfUp(5, 10)).isEqualTo(1);
			assertThat(MoneyMath.divRoundHalfUp(4, 10)).isEqualTo(0);
			assertThat(MoneyMath.divRoundHalfUp(-5, 10)).isEqualTo(-1);
			assertThat(MoneyMath.divRoundHalfUp(15, 10)).isEqualTo(2);
		}

		@Test
		void rejectsANonPositiveDenominator() {
			assertThatThrownBy(() -> MoneyMath.divRoundHalfUp(10, 0)).isInstanceOf(IllegalArgumentException.class);
			assertThatThrownBy(() -> MoneyMath.divRoundHalfUp(10, -1)).isInstanceOf(IllegalArgumentException.class);
		}

		@Test
		void failsInsteadOfOverflowing() {
			assertThatThrownBy(() -> MoneyMath.divRoundHalfUp(Long.MAX_VALUE, 10))
				.isInstanceOf(ArithmeticException.class);
		}

	}

	@Nested
	class FuelAmounts {

		@Test
		void computes40LitresOfGasoline92At19PoundsPerLitreAs760Pounds() {
			assertThat(MoneyMath.amountForVolume(40_000, 1900)).isEqualTo(76_000);
		}

		@Test
		void roundsPartialPiastresHalfUp() {
			// 42.345 L x 17.50 = 741.0375 EGP -> 741.04
			assertThat(MoneyMath.amountForVolume(42_345, 1750)).isEqualTo(74_104);
		}

		@Test
		void convertsAnAmountBackToVolume() {
			assertThat(MoneyMath.volumeForAmount(76_000, 1900)).isEqualTo(40_000);
			// 500 EGP at 19.00 = 26.315789... L -> 26.316 L
			assertThat(MoneyMath.volumeForAmount(50_000, 1900)).isEqualTo(26_316);
		}

		@Test
		void failsInsteadOfOverflowing() {
			assertThatThrownBy(() -> MoneyMath.amountForVolume(Long.MAX_VALUE, 1900))
				.isInstanceOf(ArithmeticException.class);
		}

	}

	@Nested
	class ParseDecimal {

		@Test
		void parsesUserInputIntoMinorUnits() {
			assertThat(MoneyMath.parseDecimal("42.5", 3)).hasValue(42_500);
			assertThat(MoneyMath.parseDecimal("1,200.75", 2)).hasValue(120_075);
			assertThat(MoneyMath.parseDecimal("40", 3)).hasValue(40_000);
			assertThat(MoneyMath.parseDecimal("7.", 2)).hasValue(700);
		}

		@Test
		void rejectsInvalidInputAndExcessDecimals() {
			assertThat(MoneyMath.parseDecimal("abc", 2)).isEmpty();
			assertThat(MoneyMath.parseDecimal("1.234", 2)).isEmpty();
			assertThat(MoneyMath.parseDecimal("-5", 2)).isEmpty();
		}

		@Test
		void rejectsWhatTheWebRejectsToo() {
			assertThat(MoneyMath.parseDecimal("", 2)).isEmpty();
			assertThat(MoneyMath.parseDecimal(".5", 2)).isEmpty();
			assertThat(MoneyMath.parseDecimal("٤٢", 2)).isEmpty(); // Arabic-Indic digits are not accepted
			assertThat(MoneyMath.parseDecimal("90071992547409.92", 2)).isEmpty(); // beyond the safe range
			assertThat(MoneyMath.parseDecimal(null, 2)).isEmpty();
		}

		@Test
		void treatsSpacesExactlyAsTheWebDoes() {
			assertThat(MoneyMath.parseDecimal("1 200.5", 2)).hasValue(120_050); // no-break space
			assertThat(MoneyMath.parseDecimal("1 200", 2)).hasValue(120_000); // narrow no-break space
			assertThat(MoneyMath.parseDecimal("42﻿", 2)).hasValue(4_200); // byte-order mark
			assertThat(MoneyMath.parseDecimal("4\u00852", 2)).isEmpty(); // NEL is not white space in JavaScript
		}

	}

	@Test
	void convertsWholeUnits() {
		assertThat(MoneyMath.poundsToPiastres(8_423)).isEqualTo(842_300);
		assertThat(MoneyMath.litresToMl(62)).isEqualTo(62_000);
	}

}
