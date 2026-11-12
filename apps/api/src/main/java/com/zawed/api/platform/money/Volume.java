package com.zawed.api.platform.money;

/** A fuel volume in integer millilitres. Never negative. */
public record Volume(long millilitres) implements Comparable<Volume> {

	public static final Volume ZERO = new Volume(0);

	public Volume {
		if (millilitres < 0) {
			throw new IllegalArgumentException("volume cannot be negative: " + millilitres);
		}
		MoneyMath.requireSafe(millilitres, "millilitres");
	}

	public static Volume ofMillilitres(long millilitres) {
		return new Volume(millilitres);
	}

	public static Volume ofLitres(long litres) {
		return new Volume(MoneyMath.litresToMl(litres));
	}

	public Volume plus(Volume other) {
		return new Volume(Math.addExact(millilitres, other.millilitres));
	}

	public boolean isZero() {
		return millilitres == 0;
	}

	/** Amount for this volume at a unit price per litre, rounded half-up to the piastre. */
	public Money amountAt(Money unitPricePerLitre) {
		if (unitPricePerLitre.isNegative()) {
			throw new IllegalArgumentException("unit price cannot be negative");
		}
		return new Money(MoneyMath.amountForVolume(millilitres, unitPricePerLitre.piastres()));
	}

	/** Volume that an amount buys at a unit price per litre, rounded half-up to the millilitre. */
	public static Volume boughtWith(Money amount, Money unitPricePerLitre) {
		if (amount.isNegative()) {
			throw new IllegalArgumentException("amount cannot be negative");
		}
		if (!unitPricePerLitre.isPositive()) {
			throw new IllegalArgumentException("unit price must be positive");
		}
		return new Volume(MoneyMath.volumeForAmount(amount.piastres(), unitPricePerLitre.piastres()));
	}

	@Override
	public int compareTo(Volume other) {
		return Long.compare(millilitres, other.millilitres);
	}

}
