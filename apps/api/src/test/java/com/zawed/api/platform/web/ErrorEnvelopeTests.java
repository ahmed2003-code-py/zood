package com.zawed.api.platform.web;

import static org.assertj.core.api.Assertions.assertThat;

import jakarta.servlet.RequestDispatcher;

import org.junit.jupiter.api.Test;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.mock.web.MockHttpServletRequest;

/**
 * The failure paths no public route can trigger yet: an unexpected exception and the container's
 * last-resort error dispatch. Both must answer with the envelope and reveal nothing internal.
 */
class ErrorEnvelopeTests {

	@Test
	void anUnexpectedExceptionBecomesAGeneric500WithTheRequestId() {
		MockHttpServletRequest request = requestWithId("req-500");

		ResponseEntity<Object> response = new GlobalExceptionHandler().handleUnexpected(
				new IllegalStateException("password=hunter2 at com.zawed.secret.Thing"), request);

		assertThat(response.getStatusCode()).isEqualTo(HttpStatus.INTERNAL_SERVER_ERROR);
		ApiError body = (ApiError) response.getBody();
		assertThat(body).isNotNull();
		assertThat(body.code()).isEqualTo(ErrorCodes.INTERNAL_ERROR);
		assertThat(body.retryable()).isTrue();
		assertThat(body.requestId()).isEqualTo("req-500");
		assertThat(body.message()).doesNotContain("hunter2").doesNotContain("com.zawed");
	}

	@Test
	void anApiExceptionKeepsItsCodeFieldAndParams() {
		ApiException refusal = new ApiException(HttpStatus.CONFLICT, "TOPUP_PENDING", "A top-up is already under review",
				"amount", java.util.Map.of("max", 1));

		ResponseEntity<Object> response = new GlobalExceptionHandler().handleApiException(refusal,
				requestWithId("req-409"));

		ApiError body = (ApiError) response.getBody();
		assertThat(response.getStatusCode()).isEqualTo(HttpStatus.CONFLICT);
		assertThat(body.code()).isEqualTo("TOPUP_PENDING");
		assertThat(body.field()).isEqualTo("amount");
		assertThat(body.params()).containsEntry("max", 1);
		assertThat(body.retryable()).isFalse();
	}

	@Test
	void theContainerErrorDispatchAnswersWithTheEnvelope() {
		MockHttpServletRequest request = requestWithId("req-fallback");
		request.setAttribute(RequestDispatcher.ERROR_STATUS_CODE, 503);

		ResponseEntity<ApiError> response = new FallbackErrorController().error(request);

		assertThat(response.getStatusCode()).isEqualTo(HttpStatus.SERVICE_UNAVAILABLE);
		assertThat(response.getBody().code()).isEqualTo("SERVICE_UNAVAILABLE");
		assertThat(response.getBody().retryable()).isTrue();
		assertThat(response.getBody().requestId()).isEqualTo("req-fallback");
	}

	@Test
	void theContainerErrorDispatchWithoutAStatusIsA500() {
		ResponseEntity<ApiError> response = new FallbackErrorController().error(requestWithId("req-x"));

		assertThat(response.getStatusCode()).isEqualTo(HttpStatus.INTERNAL_SERVER_ERROR);
		assertThat(response.getBody().code()).isEqualTo(ErrorCodes.INTERNAL_ERROR);
	}

	private static MockHttpServletRequest requestWithId(String requestId) {
		MockHttpServletRequest request = new MockHttpServletRequest("GET", "/api/v1/anything");
		request.setAttribute(RequestIdFilter.ATTRIBUTE, requestId);
		return request;
	}

}
