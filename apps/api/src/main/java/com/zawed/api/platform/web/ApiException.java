package com.zawed.api.platform.web;

import java.util.Map;

import org.springframework.http.HttpStatus;

/**
 * A refusal with a stable code. Throw it from anywhere below a controller:
 *
 * <pre>{@code
 * throw new ApiException(HttpStatus.CONFLICT, "TOPUP_PENDING", "A top-up is already under review");
 * }</pre>
 *
 * The message is an English fallback for logs and API clients, never the UI copy. Params carry the
 * numbers a translated sentence needs (limits, sizes) and never internal ids or secrets.
 */
public class ApiException extends RuntimeException {

	private final HttpStatus status;
	private final String code;
	private final String field;
	private final Map<String, Object> params;

	public ApiException(HttpStatus status, String code, String message) {
		this(status, code, message, null, null);
	}

	public ApiException(HttpStatus status, String code, String message, String field, Map<String, Object> params) {
		super(message);
		this.status = status;
		this.code = code;
		this.field = field;
		this.params = params == null || params.isEmpty() ? null : Map.copyOf(params);
	}

	/**
	 * For a row that does not exist and for a row outside the caller's scope alike: a caller must not
	 * be able to tell the two apart.
	 */
	public static ApiException notFound(String entity) {
		return new ApiException(HttpStatus.NOT_FOUND, ErrorCodes.NOT_FOUND, entity + " not found");
	}

	public HttpStatus status() {
		return status;
	}

	public String code() {
		return code;
	}

	public String field() {
		return field;
	}

	public Map<String, Object> params() {
		return params;
	}

}
