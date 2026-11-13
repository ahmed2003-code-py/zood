package com.zawed.api.platform.persistence;

import java.sql.ResultSet;
import java.sql.SQLException;
import java.sql.Timestamp;
import java.time.Instant;

/** Small conversions every store needs. Timestamps are UTC instants, end to end. */
public final class Sql {

	private Sql() {
	}

	public static Timestamp ts(Instant instant) {
		return instant == null ? null : Timestamp.from(instant);
	}

	public static Instant instant(ResultSet rs, String column) throws SQLException {
		Timestamp timestamp = rs.getTimestamp(column);
		return timestamp == null ? null : timestamp.toInstant();
	}

	public static Long nullableLong(ResultSet rs, String column) throws SQLException {
		long value = rs.getLong(column);
		return rs.wasNull() ? null : value;
	}

	public static Integer nullableInt(ResultSet rs, String column) throws SQLException {
		int value = rs.getInt(column);
		return rs.wasNull() ? null : value;
	}

	/** Trims, and turns an empty string into null, so optional text is stored one way only. */
	public static String clean(String value) {
		if (value == null) {
			return null;
		}
		String trimmed = value.strip().replaceAll("\\s+", " ");
		return trimmed.isEmpty() ? null : trimmed;
	}

}
