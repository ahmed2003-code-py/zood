package com.zawed.api.platform.web;

import java.util.List;
import java.util.UUID;
import java.util.function.Function;

import io.swagger.v3.oas.annotations.media.Schema;
import io.swagger.v3.oas.annotations.media.Schema.RequiredMode;

/**
 * The one list envelope: items plus a cursor for the next page. Every list endpoint is paginated
 * (no unbounded lists, Nagahny review #5). Ids are UUIDv7, so ordering by id is ordering by time.
 */
public record Page<T>(@Schema(requiredMode = RequiredMode.REQUIRED) List<T> items,
		@Schema(description = "Pass as ?cursor= to get the next page; null on the last page") String nextCursor) {

	public static final int DEFAULT_LIMIT = 25;
	public static final int MAX_LIMIT = 100;

	/** The limit a request may ask for, clamped to 1–100. */
	public static int limit(Integer requested) {
		if (requested == null) {
			return DEFAULT_LIMIT;
		}
		return Math.max(1, Math.min(MAX_LIMIT, requested));
	}

	/**
	 * Builds a page from rows fetched with {@code limit + 1}: the extra row only says there is more.
	 */
	public static <T> Page<T> of(List<T> rows, int limit, Function<T, UUID> idOf) {
		if (rows.size() <= limit) {
			return new Page<>(List.copyOf(rows), null);
		}
		List<T> items = List.copyOf(rows.subList(0, limit));
		return new Page<>(items, idOf.apply(items.getLast()).toString());
	}

	/** A cursor the client sent back; anything that is not an id is a bad request, never a 500. */
	public static UUID cursor(String value) {
		if (value == null || value.isBlank()) {
			return null;
		}
		try {
			return UUID.fromString(value);
		}
		catch (IllegalArgumentException ex) {
			throw new ApiException(org.springframework.http.HttpStatus.BAD_REQUEST, "INVALID_CURSOR", "Invalid cursor",
					"cursor", null);
		}
	}

}
