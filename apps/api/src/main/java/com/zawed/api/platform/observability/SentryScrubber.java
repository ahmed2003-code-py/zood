package com.zawed.api.platform.observability;

import java.util.ArrayList;
import java.util.List;
import java.util.Set;

import io.sentry.Hint;
import io.sentry.SentryEvent;
import io.sentry.SentryOptions;
import io.sentry.protocol.SentryException;

import org.springframework.stereotype.Component;

/**
 * Removes everything personal or financial from an error report before it leaves the server
 * (B7, owner decision 2026-11-19: Sentry, without personal or financial data).
 *
 * <ul>
 * <li>The request goes: its headers carry tokens and cookies, its query string and body carry
 * plates, names, amounts and bank references. The user goes too.
 * <li>Breadcrumbs go: they are earlier log lines, and some quote file or row content.
 * <li>An exception keeps its type and stack trace, which is what a fix needs. Its message is kept
 * only for our own exceptions and for plain programming errors, whose messages name code, not data.
 * Every other message is replaced: a PostgreSQL error carries the whole failing row ("Failing row
 * contains ..."), it reaches us wrapped in other types (an {@code IllegalStateException}, a Spring
 * {@code DataAccessException}), and a parsing error quotes the value it could not read.
 * </ul>
 *
 * The log message of a logged error stays: our log lines carry ids and counts only.
 */
@Component
public class SentryScrubber implements SentryOptions.BeforeSendCallback {

	static final String REDACTED = "[redacted: message may contain data]";

	private static final String OWN_PACKAGE = "com.zawed";

	/** Exceptions whose message describes the code (a null, a bad cast or index), never a value. */
	private static final Set<String> SAFE_MESSAGES = Set.of("java.lang.NullPointerException",
			"java.lang.ClassCastException", "java.lang.ArithmeticException", "java.lang.ArrayIndexOutOfBoundsException",
			"java.lang.IndexOutOfBoundsException", "java.lang.StringIndexOutOfBoundsException",
			"java.lang.UnsupportedOperationException", "java.util.ConcurrentModificationException",
			"java.lang.StackOverflowError", "java.lang.OutOfMemoryError");

	@Override
	public SentryEvent execute(SentryEvent event, Hint hint) {
		event.setRequest(null);
		event.setUser(null);
		event.setBreadcrumbs(new ArrayList<>());
		event.setServerName(null);
		List<SentryException> exceptions = event.getExceptions();
		if (exceptions != null) {
			for (SentryException exception : exceptions) {
				if (!messageIsSafe(exception)) {
					exception.setValue(exception.getValue() == null ? null : REDACTED);
				}
			}
		}
		return event;
	}

	private static boolean messageIsSafe(SentryException exception) {
		String module = exception.getModule() == null ? "" : exception.getModule();
		String name = module.isEmpty() ? exception.getType() : module + "." + exception.getType();
		if (module.startsWith(OWN_PACKAGE)) {
			return true;
		}
		return name != null && SAFE_MESSAGES.contains(name);
	}

}
