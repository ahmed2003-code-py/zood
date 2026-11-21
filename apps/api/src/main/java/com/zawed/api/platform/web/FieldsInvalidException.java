package com.zawed.api.platform.web;

import java.util.List;

import org.springframework.http.HttpStatus;

import com.zawed.api.platform.web.ApiError.FieldIssue;

/**
 * A validation failure found below the controller, where bean validation cannot see it (it needs the
 * stored row, for example). Answers the same {@code 400 VALIDATION_FAILED} with {@code fields} as
 * bean validation does, so a client handles both alike.
 */
public class FieldsInvalidException extends ApiException {

	private final List<FieldIssue> fields;

	public FieldsInvalidException(List<FieldIssue> fields) {
		super(HttpStatus.BAD_REQUEST, ErrorCodes.VALIDATION_FAILED, "The request is not valid");
		this.fields = List.copyOf(fields);
	}

	public List<FieldIssue> fields() {
		return fields;
	}

}
