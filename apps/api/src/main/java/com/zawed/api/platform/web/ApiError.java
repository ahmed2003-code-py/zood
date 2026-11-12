package com.zawed.api.platform.web;

import java.util.List;
import java.util.Map;

import com.fasterxml.jackson.annotation.JsonInclude;

import io.swagger.v3.oas.annotations.media.Schema;
import io.swagger.v3.oas.annotations.media.Schema.RequiredMode;

/**
 * The one shape every refusal and failure leaves this API in.
 *
 * <p>{@code code} is stable and machine-readable: clients choose their Arabic or English wording
 * from it and never from {@code message}, which is an English fallback for logs and tools.
 * {@code requestId} matches the {@code X-Request-Id} header and the server log line.
 */
@JsonInclude(JsonInclude.Include.NON_NULL)
@Schema(description = "The shape of every refusal and failure")
public record ApiError(@Schema(requiredMode = RequiredMode.REQUIRED) int statusCode,
		@Schema(requiredMode = RequiredMode.REQUIRED,
				description = "Stable reason; clients pick their wording from this") String code,
		@Schema(requiredMode = RequiredMode.REQUIRED,
				description = "English fallback for logs and tools; not UI copy") String message,
		@Schema(requiredMode = RequiredMode.REQUIRED,
				description = "Whether sending the same request again can succeed") boolean retryable,
		@Schema(description = "Matches the X-Request-Id header and the server log") String requestId,
		@Schema(description = "The single input this is about, when there is exactly one") String field,
		List<FieldIssue> fields,
		@Schema(description = "Values a translated sentence needs, such as limits") Map<String, Object> params,
		@Schema(description = "With 429: seconds until the request would be accepted") Integer retryAfterSeconds) {

	/** One input's complaint in a validation failure. */
	@JsonInclude(JsonInclude.Include.NON_NULL)
	public record FieldIssue(@Schema(requiredMode = RequiredMode.REQUIRED) String field,
			@Schema(requiredMode = RequiredMode.REQUIRED) String code, String message) {
	}

}
