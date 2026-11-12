package com.zawed.api.platform.web;

import org.springframework.http.HttpStatusCode;

/**
 * Category codes: what a refusal carries when nothing more specific was named. A refusal that
 * deserves its own sentence in the UI is thrown as an {@link ApiException} with its own code.
 */
public final class ErrorCodes {

	public static final String VALIDATION_FAILED = "VALIDATION_FAILED";
	public static final String UNAUTHENTICATED = "UNAUTHENTICATED";
	public static final String FORBIDDEN = "FORBIDDEN";
	public static final String NOT_FOUND = "NOT_FOUND";
	public static final String INTERNAL_ERROR = "INTERNAL_ERROR";

	private ErrorCodes() {
	}

	public static String generic(HttpStatusCode status) {
		return switch (status.value()) {
			case 400 -> "BAD_REQUEST";
			case 401 -> UNAUTHENTICATED;
			case 403 -> FORBIDDEN;
			case 404 -> NOT_FOUND;
			case 405 -> "METHOD_NOT_ALLOWED";
			case 406 -> "NOT_ACCEPTABLE";
			case 409 -> "CONFLICT";
			case 410 -> "GONE";
			case 413 -> "PAYLOAD_TOO_LARGE";
			case 415 -> "UNSUPPORTED_MEDIA_TYPE";
			case 422 -> "UNPROCESSABLE";
			case 429 -> "RATE_LIMITED";
			case 502 -> "UPSTREAM_ERROR";
			case 503 -> "SERVICE_UNAVAILABLE";
			case 504 -> "UPSTREAM_TIMEOUT";
			default -> status.is5xxServerError() ? INTERNAL_ERROR : "BAD_REQUEST";
		};
	}

	/** Sending the same request again can only help when the failure was not about the request. */
	public static boolean retryableByDefault(HttpStatusCode status) {
		return status.value() == 429 || status.is5xxServerError();
	}

}
