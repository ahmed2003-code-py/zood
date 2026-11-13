package com.zawed.api.rules;

import java.time.DayOfWeek;
import java.time.Instant;
import java.time.LocalDate;
import java.time.ZoneId;
import java.time.temporal.TemporalAdjusters;

/**
 * Limit periods, evaluated in the station's time zone (PRD 2.3: store UTC, evaluate business rules
 * in the station's configured zone). A week starts on Saturday, the Egyptian work week. Ported from
 * the demo's {@code calendar.ts}, whose behaviour the golden tests pin.
 */
public final class Periods {

	/** A limit's period. */
	public enum Period {

		DAY, WEEK, MONTH

	}

	private Periods() {
	}

	/** The local date the period containing {@code at} starts on. */
	public static LocalDate startDate(Instant at, Period period, ZoneId zone) {
		LocalDate day = at.atZone(zone).toLocalDate();
		return switch (period) {
			case DAY -> day;
			case WEEK -> day.with(TemporalAdjusters.previousOrSame(DayOfWeek.SATURDAY));
			case MONTH -> day.withDayOfMonth(1);
		};
	}

	/**
	 * The instant the period containing {@code at} starts: local midnight of its first day. Uses the
	 * zone's own rules, so a day after a daylight-saving change still starts at local midnight.
	 */
	public static Instant start(Instant at, Period period, ZoneId zone) {
		return startDate(at, period, zone).atStartOfDay(zone).toInstant();
	}

	/** Two instants fall in the same period exactly when their periods start on the same day. */
	public static boolean samePeriod(Instant a, Instant b, Period period, ZoneId zone) {
		return startDate(a, period, zone).equals(startDate(b, period, zone));
	}

}
