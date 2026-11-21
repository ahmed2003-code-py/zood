package com.zawed.api.platform.web;

import java.util.List;
import java.util.Map;

import jakarta.servlet.http.HttpServletRequest;
import jakarta.validation.ConstraintViolation;
import jakarta.validation.ConstraintViolationException;

import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.http.HttpHeaders;
import org.springframework.http.HttpStatus;
import org.springframework.http.HttpStatusCode;
import org.springframework.http.MediaType;
import org.springframework.http.ResponseEntity;
import org.springframework.http.converter.HttpMessageNotReadableException;
import org.springframework.security.access.AccessDeniedException;
import org.springframework.security.core.AuthenticationException;
import org.springframework.web.bind.MethodArgumentNotValidException;
import org.springframework.web.bind.annotation.ExceptionHandler;
import org.springframework.web.bind.annotation.RestControllerAdvice;
import org.springframework.web.context.request.ServletWebRequest;
import org.springframework.web.context.request.WebRequest;
import org.springframework.web.servlet.mvc.method.annotation.ResponseEntityExceptionHandler;

import tools.jackson.core.JacksonException;
import tools.jackson.databind.exc.InvalidFormatException;

import com.zawed.api.platform.ratelimit.RateLimitedException;
import com.zawed.api.platform.web.ApiError.FieldIssue;

/**
 * Turns every exception into the {@link ApiError} envelope. Nothing internal reaches a response:
 * an unexpected failure is logged with its stack trace and answered with a generic 500 that
 * carries only the request id.
 */
@RestControllerAdvice
class GlobalExceptionHandler extends ResponseEntityExceptionHandler {

	private static final Logger log = LoggerFactory.getLogger(GlobalExceptionHandler.class);

	@ExceptionHandler(ApiException.class)
	ResponseEntity<Object> handleApiException(ApiException ex, HttpServletRequest request) {
		if (ex instanceof RateLimitedException limited) {
			HttpHeaders headers = new HttpHeaders();
			headers.set(HttpHeaders.RETRY_AFTER, String.valueOf(limited.retryAfterSeconds()));
			ApiError body = new ApiError(ex.status().value(), ex.code(), ex.getMessage(), true,
					RequestIdFilter.currentRequestId(request), null, null, ex.params(),
					(int) Math.min(Integer.MAX_VALUE, limited.retryAfterSeconds()));
			return ResponseEntity.status(ex.status()).headers(headers).contentType(MediaType.APPLICATION_JSON).body(body);
		}
		List<FieldIssue> fields = ex instanceof FieldsInvalidException invalid ? invalid.fields() : null;
		return respond(ex.status(), ex.code(), ex.getMessage(), ex.field(), fields, ex.params(), new HttpHeaders(),
				request);
	}

	/** Reached from the security filters through {@code SecurityErrorHandler}. */
	@ExceptionHandler(AuthenticationException.class)
	ResponseEntity<Object> handleUnauthenticated(AuthenticationException ex, HttpServletRequest request) {
		return respond(HttpStatus.UNAUTHORIZED, ErrorCodes.UNAUTHENTICATED, "Authentication is required", null, null,
				null, new HttpHeaders(), request);
	}

	@ExceptionHandler(AccessDeniedException.class)
	ResponseEntity<Object> handleForbidden(AccessDeniedException ex, HttpServletRequest request) {
		return respond(HttpStatus.FORBIDDEN, ErrorCodes.FORBIDDEN, "You do not have permission to do this", null,
				null, null, new HttpHeaders(), request);
	}

	@ExceptionHandler(ConstraintViolationException.class)
	ResponseEntity<Object> handleConstraintViolation(ConstraintViolationException ex, HttpServletRequest request) {
		List<FieldIssue> fields = ex.getConstraintViolations().stream().map(GlobalExceptionHandler::issueOf).toList();
		return respond(HttpStatus.BAD_REQUEST, ErrorCodes.VALIDATION_FAILED, "The request is not valid", null, fields,
				null, new HttpHeaders(), request);
	}

	@ExceptionHandler(Exception.class)
	ResponseEntity<Object> handleUnexpected(Exception ex, HttpServletRequest request) {
		log.error("Unhandled exception on {} {}", request.getMethod(), request.getRequestURI(), ex);
		return respond(HttpStatus.INTERNAL_SERVER_ERROR, ErrorCodes.INTERNAL_ERROR, "Unexpected error", null, null,
				null, new HttpHeaders(), request);
	}

	@Override
	protected ResponseEntity<Object> handleMethodArgumentNotValid(MethodArgumentNotValidException ex,
			HttpHeaders headers, HttpStatusCode status, WebRequest request) {
		List<FieldIssue> fields = ex.getBindingResult()
			.getFieldErrors()
			.stream()
			.map((error) -> new FieldIssue(error.getField(), constraintCode(error.getCode()), error.getDefaultMessage()))
			.toList();
		return respond(status, ErrorCodes.VALIDATION_FAILED, "The request is not valid", null, fields, null, headers,
				servletRequest(request));
	}

	/**
	 * A fractional number where a whole one is required (money in piastres, volume in millilitres, a
	 * count) is a validation failure on that field, not an unreadable body: the client sent a value of
	 * the right kind that breaks a rule, and the person typing it must be told which field. Jackson
	 * refuses it because {@code accept-float-as-int} is off (application.yaml). Every other unreadable
	 * body (broken JSON, an unknown field, a wrong type) stays the generic 400 BAD_REQUEST.
	 */
	@Override
	protected ResponseEntity<Object> handleHttpMessageNotReadable(HttpMessageNotReadableException ex,
			HttpHeaders headers, HttpStatusCode status, WebRequest request) {
		InvalidFormatException invalid = fractionForWholeNumber(ex);
		if (invalid != null) {
			List<FieldIssue> fields = List.of(new FieldIssue(pathOf(invalid), "INTEGER_REQUIRED",
					"must be a whole number"));
			return respond(HttpStatus.BAD_REQUEST, ErrorCodes.VALIDATION_FAILED, "The request is not valid", null, fields,
					null, headers, servletRequest(request));
		}
		return super.handleHttpMessageNotReadable(ex, headers, status, request);
	}

	private static InvalidFormatException fractionForWholeNumber(Throwable ex) {
		for (Throwable cause = ex; cause != null; cause = cause.getCause()) {
			if (cause instanceof InvalidFormatException invalid && invalid.getValue() instanceof Number
					&& isWholeNumberType(invalid.getTargetType())) {
				return invalid;
			}
			if (cause.getCause() == cause) {
				break;
			}
		}
		return null;
	}

	private static boolean isWholeNumberType(Class<?> type) {
		return type == Long.class || type == long.class || type == Integer.class || type == int.class
				|| type == Short.class || type == short.class || type == java.math.BigInteger.class;
	}

	/** {@code items[0].volumeMl}, the way bean validation names a nested field. */
	private static String pathOf(JacksonException ex) {
		StringBuilder path = new StringBuilder();
		for (JacksonException.Reference reference : ex.getPath()) {
			if (reference.getPropertyName() != null) {
				if (!path.isEmpty()) {
					path.append('.');
				}
				path.append(reference.getPropertyName());
			}
			else if (reference.getIndex() >= 0) {
				path.append('[').append(reference.getIndex()).append(']');
			}
		}
		return path.isEmpty() ? null : path.toString();
	}

	/** Every other exception Spring MVC raises itself (unreadable body, wrong method, no such route). */
	@Override
	protected ResponseEntity<Object> handleExceptionInternal(Exception ex, Object body, HttpHeaders headers,
			HttpStatusCode status, WebRequest request) {
		if (status.is5xxServerError()) {
			log.error("Request failed with {}", status.value(), ex);
		}
		return respond(status, ErrorCodes.generic(status), safeMessage(status), null, null, null, headers,
				servletRequest(request));
	}

	private static ResponseEntity<Object> respond(HttpStatusCode status, String code, String message, String field,
			List<FieldIssue> fields, Map<String, Object> params, HttpHeaders headers, HttpServletRequest request) {
		ApiError body = new ApiError(status.value(), code, message, ErrorCodes.retryableByDefault(status),
				RequestIdFilter.currentRequestId(request), field, fields, params, null);
		return ResponseEntity.status(status).headers(headers).contentType(MediaType.APPLICATION_JSON).body(body);
	}

	/**
	 * The status reason phrase only. Neither the exception message nor Spring's own detail is used:
	 * several details repeat the request (the path, a header value), and nothing from the request is
	 * echoed back.
	 */
	private static String safeMessage(HttpStatusCode status) {
		HttpStatus known = HttpStatus.resolve(status.value());
		return known != null ? known.getReasonPhrase() : "Request failed";
	}

	private static FieldIssue issueOf(ConstraintViolation<?> violation) {
		String path = violation.getPropertyPath().toString();
		String field = path.substring(path.lastIndexOf('.') + 1);
		String constraint = violation.getConstraintDescriptor().getAnnotation().annotationType().getSimpleName();
		return new FieldIssue(field, constraintCode(constraint), violation.getMessage());
	}

	/** {@code NotBlank} becomes {@code NOT_BLANK}: a stable code the client can translate. */
	private static String constraintCode(String constraintName) {
		if (constraintName == null || constraintName.isBlank()) {
			return "INVALID";
		}
		return constraintName.replaceAll("([a-z0-9])([A-Z])", "$1_$2").toUpperCase();
	}

	private static HttpServletRequest servletRequest(WebRequest request) {
		return ((ServletWebRequest) request).getRequest();
	}

}
