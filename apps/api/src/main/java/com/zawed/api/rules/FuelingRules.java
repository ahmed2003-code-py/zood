package com.zawed.api.rules;

import java.time.Instant;
import java.util.List;
import java.util.Objects;
import java.util.Optional;

import com.zawed.api.platform.FuelType;
import com.zawed.api.platform.money.MoneyMath;
import com.zawed.api.rules.Periods.Period;

/**
 * The fueling decision (PRD 6.1), as a pure function: the engine gathers the facts under its locks,
 * this decides. No Spring, no database, no clock: everything arrives as input, so the same rules run
 * on the server online (B4), at offline ingest (B5) and can be checked by golden tests.
 *
 * <p>Ported from the demo's {@code checkFueling} in {@code fueling.ts}; the order of checks is the
 * PRD's and is part of the contract: the tag's cooldown first, then tag, vehicle, company, fuel,
 * limits, wallet. The first failing check is the reason given.
 */
public final class FuelingRules {

	/** Why a fueling is declined. Order of declaration = order of checks. */
	public enum DeclineReason {

		COOLDOWN, TAG_INACTIVE, VEHICLE_INACTIVE, COMPANY_FROZEN, FUEL_TYPE_MISMATCH, FUEL_NOT_AVAILABLE,
		LIMIT_EXCEEDED, INSUFFICIENT_BALANCE

	}

	/** What a limit counts. */
	public enum LimitKind {

		/** Millilitres in the period. */
		VOLUME,
		/** Piastres in the period. */
		AMOUNT

	}

	public record Tag(boolean active, Instant cooldownUntil) {
	}

	public record Vehicle(boolean active, FuelType fuelType, boolean vip, long tankCapacityMl) {

		public Vehicle {
			Objects.requireNonNull(fuelType, "fuelType");
		}

	}

	public record Company(boolean active) {
	}

	/**
	 * A limit with what the vehicle has already used in the period that contains the evaluation time
	 * (completed fuelings only), in the limit's unit.
	 */
	public record Limit(LimitKind kind, Period period, long maxValue, long used) {

		public Limit {
			Objects.requireNonNull(kind, "kind");
			Objects.requireNonNull(period, "period");
		}

	}

	/**
	 * Everything the decision needs.
	 *
	 * @param requestedFuel the fuel the agent selected, or null when the vehicle's fuel is implied
	 * @param unitPricePiastres the price per litre in force at the station, or null when the station
	 * does not sell the vehicle's fuel
	 * @param walletBalancePiastres read under the wallet's row lock
	 */
	public record Facts(Instant evaluatedAt, Tag tag, Vehicle vehicle, Company company, FuelType requestedFuel,
			Long unitPricePiastres, List<Limit> limits, long walletBalancePiastres, long volumeMl) {

		public Facts {
			Objects.requireNonNull(evaluatedAt, "evaluatedAt");
			Objects.requireNonNull(tag, "tag");
			Objects.requireNonNull(vehicle, "vehicle");
			Objects.requireNonNull(company, "company");
			limits = List.copyOf(limits);
			if (volumeMl <= 0) {
				throw new IllegalArgumentException("A fueling's volume must be positive: " + volumeMl);
			}
		}

	}

	/**
	 * The outcome.
	 *
	 * @param amountPiastres the price of the volume (0 when the fuel is not sold); charged only when
	 * approved and billable
	 * @param billable VIP fuelings are recorded but not charged (PRD 4.4)
	 * @param tankCapacityExceeded more than the tank holds: approved, but flagged to Operations
	 * (PRD 10)
	 */
	public record Decision(Optional<DeclineReason> declineReason, long amountPiastres, long unitPricePiastres,
			boolean billable, boolean tankCapacityExceeded) {

		public boolean approved() {
			return declineReason.isEmpty();
		}

	}

	private FuelingRules() {
	}

	public static Decision decide(Facts facts) {
		long price = facts.unitPricePiastres() == null ? 0 : facts.unitPricePiastres();
		long amount = price == 0 ? 0 : MoneyMath.amountForVolume(facts.volumeMl(), price);
		boolean billable = !facts.vehicle().vip();
		boolean overTank = facts.volumeMl() > facts.vehicle().tankCapacityMl();
		return new Decision(firstFailure(facts, amount), amount, price, billable, overTank);
	}

	private static Optional<DeclineReason> firstFailure(Facts facts, long amount) {
		// PRD 6.1: the tag's cooldown is checked first, before any other validation.
		Instant cooldownUntil = facts.tag().cooldownUntil();
		if (cooldownUntil != null && cooldownUntil.isAfter(facts.evaluatedAt())) {
			return Optional.of(DeclineReason.COOLDOWN);
		}
		if (!facts.tag().active()) {
			return Optional.of(DeclineReason.TAG_INACTIVE);
		}
		if (!facts.vehicle().active()) {
			return Optional.of(DeclineReason.VEHICLE_INACTIVE);
		}
		if (!facts.company().active()) {
			return Optional.of(DeclineReason.COMPANY_FROZEN);
		}
		if (facts.requestedFuel() != null && facts.requestedFuel() != facts.vehicle().fuelType()) {
			return Optional.of(DeclineReason.FUEL_TYPE_MISMATCH);
		}
		if (facts.unitPricePiastres() == null) {
			return Optional.of(DeclineReason.FUEL_NOT_AVAILABLE);
		}
		// A VIP vehicle skips the limits and the wallet (PRD 4.4); its monthly ceiling only flags.
		if (facts.vehicle().vip()) {
			return Optional.empty();
		}
		for (Limit limit : facts.limits()) {
			long requested = limit.kind() == LimitKind.VOLUME ? facts.volumeMl() : amount;
			if (Math.addExact(limit.used(), requested) > limit.maxValue()) {
				return Optional.of(DeclineReason.LIMIT_EXCEEDED);
			}
		}
		if (facts.walletBalancePiastres() - amount < 0) {
			return Optional.of(DeclineReason.INSUFFICIENT_BALANCE);
		}
		return Optional.empty();
	}

}
