package com.zawed.api.platform.money;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import org.junit.jupiter.api.Test;

class MoneyAndVolumeTests {

	@Test
	void moneyArithmeticIsExact() {
		Money wallet = Money.ofPounds(8_423).plus(Money.ofPiastres(50)); // 842,350 piastres
		Money afterFueling = wallet.minus(Money.ofPiastres(76_000));

		assertThat(afterFueling).isEqualTo(Money.ofPiastres(766_350));
		assertThat(afterFueling.negate().isNegative()).isTrue();
		assertThat(Money.ZERO.isZero()).isTrue();
		assertThat(Money.ofPiastres(1)).isGreaterThan(Money.ZERO);
	}

	@Test
	void moneyMayBeNegative() {
		assertThat(Money.ofPiastres(100).minus(Money.ofPiastres(250))).isEqualTo(Money.ofPiastres(-150));
	}

	@Test
	void moneyRejectsValuesAJavaScriptClientCannotRepresent() {
		assertThatThrownBy(() -> Money.ofPiastres(MoneyMath.MAX_SAFE + 1)).isInstanceOf(ArithmeticException.class);
		assertThatThrownBy(() -> Money.ofPiastres(MoneyMath.MAX_SAFE).plus(Money.ofPiastres(1)))
			.isInstanceOf(ArithmeticException.class);
	}

	@Test
	void volumeIsNeverNegative() {
		assertThatThrownBy(() -> Volume.ofMillilitres(-1)).isInstanceOf(IllegalArgumentException.class);
		assertThat(Volume.ofLitres(40).plus(Volume.ofMillilitres(500))).isEqualTo(Volume.ofMillilitres(40_500));
	}

	@Test
	void pricesAVolume() {
		Money pricePerLitre = Money.ofPiastres(1900);

		assertThat(Volume.ofLitres(40).amountAt(pricePerLitre)).isEqualTo(Money.ofPounds(760));
		assertThat(Volume.ofMillilitres(42_345).amountAt(Money.ofPiastres(1750))).isEqualTo(Money.ofPiastres(74_104));
		assertThat(Volume.boughtWith(Money.ofPounds(500), pricePerLitre)).isEqualTo(Volume.ofMillilitres(26_316));
	}

	@Test
	void refusesToPriceWithANonPositiveUnitPrice() {
		assertThatThrownBy(() -> Volume.boughtWith(Money.ofPounds(500), Money.ZERO))
			.isInstanceOf(IllegalArgumentException.class);
		assertThatThrownBy(() -> Volume.ofLitres(1).amountAt(Money.ofPiastres(-1)))
			.isInstanceOf(IllegalArgumentException.class);
	}

}
