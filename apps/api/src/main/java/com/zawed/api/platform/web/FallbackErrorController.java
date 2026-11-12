package com.zawed.api.platform.web;

import jakarta.servlet.RequestDispatcher;
import jakarta.servlet.http.HttpServletRequest;

import io.swagger.v3.oas.annotations.Hidden;

import org.springframework.boot.webmvc.error.ErrorController;
import org.springframework.http.HttpStatus;
import org.springframework.http.MediaType;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

/**
 * The last resort: a failure that never reached a controller (a filter that threw, a request the
 * container itself refused) is dispatched here by the servlet container. It answers with the same
 * envelope as everything else and reveals nothing but the status.
 */
@RestController
@Hidden
class FallbackErrorController implements ErrorController {

	@RequestMapping("${server.error.path:/error}")
	ResponseEntity<ApiError> error(HttpServletRequest request) {
		Object code = request.getAttribute(RequestDispatcher.ERROR_STATUS_CODE);
		HttpStatus status = code instanceof Integer value ? HttpStatus.resolve(value) : null;
		if (status == null) {
			status = HttpStatus.INTERNAL_SERVER_ERROR;
		}
		ApiError body = new ApiError(status.value(), ErrorCodes.generic(status), status.getReasonPhrase(),
				ErrorCodes.retryableByDefault(status), RequestIdFilter.currentRequestId(request), null, null, null,
				null);
		return ResponseEntity.status(status).contentType(MediaType.APPLICATION_JSON).body(body);
	}

}
