package com.zawed.api.platform.observability;

import static org.assertj.core.api.Assertions.assertThat;

import java.util.List;
import java.util.Map;

import io.sentry.Breadcrumb;
import io.sentry.Hint;
import io.sentry.SentryEvent;
import io.sentry.protocol.Request;
import io.sentry.protocol.SentryException;
import io.sentry.protocol.User;

import org.junit.jupiter.api.Test;

/** Nothing personal or financial leaves the server in an error report. */
class SentryScrubberTests {

	private final SentryScrubber scrubber = new SentryScrubber();

	@Test
	void theRequestTheUserAndTheBreadcrumbsAreRemoved() {
		SentryEvent event = new SentryEvent();
		Request request = new Request();
		request.setUrl("https://api.example/api/v1/ops/vehicles?plate=%D8%A3%D8%A8%D8%AC%201234");
		request.setHeaders(Map.of("Authorization", "Bearer secret", "Cookie", "zawed_refresh=secret"));
		request.setData("{\"bankReference\":\"FT123\",\"amount\":500000}");
		event.setRequest(request);
		User user = new User();
		user.setEmail("someone@example.com");
		event.setUser(user);
		event.addBreadcrumb(new Breadcrumb("Fleet import row: plate أبج 1234"));
		event.setServerName("host-1");

		SentryEvent sent = scrubber.execute(event, new Hint());

		assertThat(sent.getRequest()).isNull();
		assertThat(sent.getUser()).isNull();
		assertThat(sent.getBreadcrumbs()).isEmpty();
		assertThat(sent.getServerName()).isNull();
	}

	@Test
	void aDatabaseMessageIsRedactedWhileTheTypeIsKept() {
		SentryEvent event = new SentryEvent();
		SentryException wrapped = exception("java.lang", "IllegalStateException",
				"org.postgresql.util.PSQLException: ERROR: new row violates check constraint");
		SentryException database = exception("org.postgresql.util", "PSQLException",
				"Detail: Failing row contains (…, TST C83506E0, Toyota, Hilux, 2022, …)");
		SentryException parsing = exception("com.fasterxml.jackson.databind.exc", "InvalidFormatException",
				"Cannot deserialize value of type `FuelType` from String \"PLUTONIUM\"");
		event.setExceptions(List.of(wrapped, database, parsing));

		scrubber.execute(event, new Hint());

		assertThat(List.of(wrapped, database, parsing)).allSatisfy(e -> assertThat(e.getValue()).isEqualTo(SentryScrubber.REDACTED));
		assertThat(database.getType()).isEqualTo("PSQLException");
	}

	@Test
	void ourOwnAndPlainProgrammingErrorMessagesAreKept() {
		SentryEvent event = new SentryEvent();
		SentryException own = exception("com.zawed.api.platform.web", "ApiException", "The tank capacity must be between 1 ml and 1,500 L");
		SentryException npe = exception("java.lang", "NullPointerException", "Cannot invoke \"Object.toString()\" because \"x\" is null");
		event.setExceptions(List.of(own, npe));

		scrubber.execute(event, new Hint());

		assertThat(own.getValue()).startsWith("The tank capacity");
		assertThat(npe.getValue()).startsWith("Cannot invoke");
	}

	private static SentryException exception(String module, String type, String value) {
		SentryException exception = new SentryException();
		exception.setModule(module);
		exception.setType(type);
		exception.setValue(value);
		return exception;
	}

}
