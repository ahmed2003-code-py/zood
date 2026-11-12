package com.zawed.api.platform.money;

/**
 * An amount of money in integer piastres. It may be negative: a debit, or a wallet pushed below
 * zero by an offline capture (PRD §4.7). Arithmetic is exact and fails on overflow.
 */
public record Money(long piastres) implements Comparable<Money> {

	public static final Money ZERO = new Money(0);

	public Money {
		MoneyMath.requireSafe(piastres, "piastres");
	}

	public static Money ofPiastres(long piastres) {
		return new Money(piastres);
	}

	public static Money ofPounds(long pounds) {
		return new Money(MoneyMath.poundsToPiastres(pounds));
	}

	public Money plus(Money other) {
		return new Money(Math.addExact(piastres, other.piastres));
	}

	public Money minus(Money other) {
		return new Money(Math.subtractExact(piastres, other.piastres));
	}

	public Money negate() {
		return new Money(Math.negateExact(piastres));
	}

	public boolean isNegative() {
		return piastres < 0;
	}

	public boolean isZero() {
		return piastres == 0;
	}

	public boolean isPositive() {
		return piastres > 0;
	}

	@Override
	public int compareTo(Money other) {
		return Long.compare(piastres, other.piastres);
	}

}
