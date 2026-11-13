package com.zawed.api.rules;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.time.Instant;
import java.time.LocalDate;
import java.time.ZoneId;
import java.util.List;
import java.util.function.UnaryOperator;

import org.junit.jupiter.api.Nested;
import org.junit.jupiter.api.Test;

import com.zawed.api.platform.FuelType;
import com.zawed.api.rules.FuelingRules.Company;
import com.zawed.api.rules.FuelingRules.DeclineReason;
import com.zawed.api.rules.FuelingRules.Decision;
import com.zawed.api.rules.FuelingRules.Facts;
import com.zawed.api.rules.FuelingRules.Limit;
import com.zawed.api.rules.FuelingRules.LimitKind;
import com.zawed.api.rules.FuelingRules.Tag;
import com.zawed.api.rules.FuelingRules.Vehicle;
import com.zawed.api.rules.Periods.Period;

/**
 * Golden tests for the fueling decision, ported from the demo's {@code fueling.test.ts} and
 * extended: the PRD order of checks, every boundary, and the periods in Cairo time.
 */
class FuelingRulesTests {

	private static final Instant NOW = Instant.parse("2026-11-13T09:00:00Z");
	private static final ZoneId CAIRO = ZoneId.of("Africa/Cairo");

	/** A fueling that passes every check: 30 L of 92 at 19 EGP/L = 570 EGP, from 1,000 EGP. */
	private static Facts clean() {
		return new Facts(NOW, new Tag(true, null), new Vehicle(true, FuelType.GAS_92, false, 50_000),
				new Company(true), null, 1_900L, List.of(), 100_000, 30_000);
	}

	private static Facts with(UnaryOperator<Builder> change) {
		return change.apply(new Builder(clean())).build();
	}

	@Test
	void aCleanFuelingIsApprovedAndPricedInWholePiastres() {
		Decision decision = FuelingRules.decide(clean());
		assertThat(decision.approved()).isTrue();
		assertThat(decision.amountPiastres()).isEqualTo(57_000);
		assertThat(decision.billable()).isTrue();
		assertThat(decision.tankCapacityExceeded()).isFalse();
	}

	@Test
	void theAmountRoundsHalfUpToTheNearestPiastre() {
		// 12.345 L at 19.01 EGP/L = 234.67845 EGP -> 23,468 piastres.
		Decision decision = FuelingRules.decide(with((b) -> b.volume(12_345).price(1_901L)));
		assertThat(decision.amountPiastres()).isEqualTo(23_468);
	}

	@Nested
	class OrderOfChecks {

		@Test
		void cooldownIsCheckedBeforeAnythingElse() {
			// Every other check fails too; the cooldown still wins (PRD 6.1).
			Facts everythingWrong = new Facts(NOW, new Tag(false, NOW.plusSeconds(60)),
					new Vehicle(false, FuelType.DIESEL, false, 50_000), new Company(false), FuelType.GAS_92, null,
					List.of(new Limit(LimitKind.VOLUME, Period.DAY, 1, 1)), 0, 30_000);
			assertThat(reason(everythingWrong)).isEqualTo(DeclineReason.COOLDOWN);
		}

		@Test
		void eachCheckWinsOverEveryLaterOne() {
			assertThat(reason(with((b) -> b.tagActive(false).vehicleActive(false).companyActive(false))))
				.isEqualTo(DeclineReason.TAG_INACTIVE);
			assertThat(reason(with((b) -> b.vehicleActive(false).companyActive(false)))).isEqualTo(DeclineReason.VEHICLE_INACTIVE);
			assertThat(reason(with((b) -> b.companyActive(false).requested(FuelType.DIESEL)))).isEqualTo(DeclineReason.COMPANY_FROZEN);
			assertThat(reason(with((b) -> b.requested(FuelType.DIESEL).price(null)))).isEqualTo(DeclineReason.FUEL_TYPE_MISMATCH);
			assertThat(reason(with((b) -> b.price(null).balance(0)))).isEqualTo(DeclineReason.FUEL_NOT_AVAILABLE);
			assertThat(reason(with((b) -> b.limit(new Limit(LimitKind.VOLUME, Period.DAY, 10_000, 0)).balance(0))))
				.isEqualTo(DeclineReason.LIMIT_EXCEEDED);
			assertThat(reason(with((b) -> b.balance(0)))).isEqualTo(DeclineReason.INSUFFICIENT_BALANCE);
		}

		@Test
		void anExpiredCooldownDoesNotBlock() {
			assertThat(FuelingRules.decide(with((b) -> b.cooldown(NOW))).approved()).as("ends exactly now").isTrue();
			assertThat(FuelingRules.decide(with((b) -> b.cooldown(NOW.minusSeconds(1)))).approved()).isTrue();
		}

		@Test
		void selectingTheVehiclesOwnFuelIsAllowed() {
			assertThat(FuelingRules.decide(with((b) -> b.requested(FuelType.GAS_92))).approved()).isTrue();
		}

	}

	@Nested
	class Limits {

		@Test
		void reachingAVolumeLimitExactlyIsAllowedAndOneMillilitreMoreIsNot() {
			// 50 L a day, 20 L used, 30 L requested: exactly 50.
			assertThat(FuelingRules.decide(with((b) -> b.limit(new Limit(LimitKind.VOLUME, Period.DAY, 50_000, 20_000))))
				.approved()).isTrue();
			assertThat(reason(with((b) -> b.limit(new Limit(LimitKind.VOLUME, Period.DAY, 50_000, 20_001)))))
				.isEqualTo(DeclineReason.LIMIT_EXCEEDED);
		}

		@Test
		void anAmountLimitCountsThePriceOfThisFueling() {
			// 3,000 EGP a week, 2,430 EGP used, this fueling costs 570 EGP: exactly 3,000.
			assertThat(FuelingRules.decide(with((b) -> b.limit(new Limit(LimitKind.AMOUNT, Period.WEEK, 300_000, 243_000))))
				.approved()).isTrue();
			assertThat(reason(with((b) -> b.limit(new Limit(LimitKind.AMOUNT, Period.WEEK, 300_000, 243_001)))))
				.isEqualTo(DeclineReason.LIMIT_EXCEEDED);
		}

		@Test
		void everyLimitMustHold() {
			Facts facts = with((b) -> b.limit(new Limit(LimitKind.VOLUME, Period.DAY, 200_000, 0))
				.limit(new Limit(LimitKind.AMOUNT, Period.MONTH, 50_000, 0)));
			assertThat(reason(facts)).isEqualTo(DeclineReason.LIMIT_EXCEEDED);
		}

	}

	@Nested
	class Wallet {

		@Test
		void aFuelingMayLeaveTheWalletAtZeroButNotBelow() {
			assertThat(FuelingRules.decide(with((b) -> b.balance(57_000))).approved()).isTrue();
			assertThat(reason(with((b) -> b.balance(56_999)))).isEqualTo(DeclineReason.INSUFFICIENT_BALANCE);
		}

	}

	@Nested
	class Vip {

		@Test
		void aVipVehicleSkipsLimitsAndWalletAndIsNotBilled() {
			Decision decision = FuelingRules.decide(with((b) -> b.vip(true).balance(0)
				.limit(new Limit(LimitKind.VOLUME, Period.DAY, 1, 1))));
			assertThat(decision.approved()).isTrue();
			assertThat(decision.billable()).isFalse();
		}

		@Test
		void aVipVehicleStillHonoursTheCooldownTagVehicleCompanyAndFuel() {
			assertThat(reason(with((b) -> b.vip(true).cooldown(NOW.plusSeconds(1))))).isEqualTo(DeclineReason.COOLDOWN);
			assertThat(reason(with((b) -> b.vip(true).companyActive(false)))).isEqualTo(DeclineReason.COMPANY_FROZEN);
			assertThat(reason(with((b) -> b.vip(true).price(null)))).isEqualTo(DeclineReason.FUEL_NOT_AVAILABLE);
		}

	}

	@Test
	void moreThanTheTankHoldsIsApprovedButFlagged() {
		Decision decision = FuelingRules.decide(with((b) -> b.volume(50_001).balance(1_000_000)));
		assertThat(decision.approved()).isTrue();
		assertThat(decision.tankCapacityExceeded()).isTrue();
		assertThat(FuelingRules.decide(with((b) -> b.volume(50_000).balance(1_000_000))).tankCapacityExceeded()).isFalse();
	}

	@Test
	void aVolumeMustBePositive() {
		assertThatThrownBy(() -> with((b) -> b.volume(0))).isInstanceOf(IllegalArgumentException.class);
	}

	@Nested
	class PeriodsInCairo {

		@Test
		void aDayStartsAtCairoMidnightNotUtcMidnight() {
			// 23:30 UTC on the 12th is already 01:30 on the 13th in Cairo (UTC+2 in November).
			Instant lateUtc = Instant.parse("2026-11-12T23:30:00Z");
			assertThat(Periods.startDate(lateUtc, Period.DAY, CAIRO)).isEqualTo(LocalDate.of(2026, 11, 13));
			assertThat(Periods.start(lateUtc, Period.DAY, CAIRO)).isEqualTo(Instant.parse("2026-11-12T22:00:00Z"));
			assertThat(Periods.samePeriod(lateUtc, Instant.parse("2026-11-12T21:59:59Z"), Period.DAY, CAIRO)).isFalse();
		}

		@Test
		void aWeekStartsOnSaturday() {
			// Friday 13 November 2026 belongs to the week that started on Saturday 7 November.
			assertThat(Periods.startDate(NOW, Period.WEEK, CAIRO)).isEqualTo(LocalDate.of(2026, 11, 7));
			Instant saturday = Instant.parse("2026-11-14T08:00:00Z");
			assertThat(Periods.startDate(saturday, Period.WEEK, CAIRO)).isEqualTo(LocalDate.of(2026, 11, 14));
			assertThat(Periods.samePeriod(NOW, saturday, Period.WEEK, CAIRO)).isFalse();
		}

		@Test
		void aMonthStartsOnTheFirst() {
			assertThat(Periods.startDate(NOW, Period.MONTH, CAIRO)).isEqualTo(LocalDate.of(2026, 11, 1));
			// 22:30 UTC on 31 October is 00:30 on 1 November in Cairo (UTC+2 after daylight saving).
			assertThat(Periods.startDate(Instant.parse("2026-10-31T22:30:00Z"), Period.MONTH, CAIRO))
				.isEqualTo(LocalDate.of(2026, 11, 1));
		}

		@Test
		void aDayAcrossTheDaylightSavingChangeStillStartsAtLocalMidnight() {
			// Egypt returns to UTC+2 at the end of the last Thursday of October 2026 (29 October).
			Instant afterChange = Instant.parse("2026-10-30T10:00:00Z");
			assertThat(Periods.start(afterChange, Period.DAY, CAIRO)).isEqualTo(Instant.parse("2026-10-29T22:00:00Z"));
			Instant beforeChange = Instant.parse("2026-10-28T10:00:00Z");
			assertThat(Periods.start(beforeChange, Period.DAY, CAIRO)).isEqualTo(Instant.parse("2026-10-27T21:00:00Z"));
		}

	}

	private static DeclineReason reason(Facts facts) {
		return FuelingRules.decide(facts).declineReason().orElseThrow(() -> new AssertionError("expected a decline"));
	}

	/** Changes one fact at a time from the clean fueling. */
	private static final class Builder {

		private Instant cooldown;
		private boolean tagActive;
		private boolean vehicleActive;
		private boolean vip;
		private FuelType fuel;
		private long tank;
		private boolean companyActive;
		private FuelType requested;
		private Long price;
		private List<Limit> limits;
		private long balance;
		private long volume;

		Builder(Facts facts) {
			cooldown = facts.tag().cooldownUntil();
			tagActive = facts.tag().active();
			vehicleActive = facts.vehicle().active();
			vip = facts.vehicle().vip();
			fuel = facts.vehicle().fuelType();
			tank = facts.vehicle().tankCapacityMl();
			companyActive = facts.company().active();
			requested = facts.requestedFuel();
			price = facts.unitPricePiastres();
			limits = new java.util.ArrayList<>(facts.limits());
			balance = facts.walletBalancePiastres();
			volume = facts.volumeMl();
		}

		Builder cooldown(Instant value) { cooldown = value; return this; }
		Builder tagActive(boolean value) { tagActive = value; return this; }
		Builder vehicleActive(boolean value) { vehicleActive = value; return this; }
		Builder vip(boolean value) { vip = value; return this; }
		Builder companyActive(boolean value) { companyActive = value; return this; }
		Builder requested(FuelType value) { requested = value; return this; }
		Builder price(Long value) { price = value; return this; }
		Builder limit(Limit value) { limits.add(value); return this; }
		Builder balance(long value) { balance = value; return this; }
		Builder volume(long value) { volume = value; return this; }

		Facts build() {
			return new Facts(NOW, new Tag(tagActive, cooldown), new Vehicle(vehicleActive, fuel, vip, tank),
					new Company(companyActive), requested, price, limits, balance, volume);
		}

	}

}
