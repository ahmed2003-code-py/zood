package com.zawed.api.platform.jobs;

import static org.assertj.core.api.Assertions.assertThat;
import static org.mockito.Mockito.when;

import java.net.http.HttpResponse;
import java.time.Duration;
import java.time.Instant;
import java.util.List;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.test.context.bean.override.mockito.MockitoBean;
import org.springframework.test.util.ReflectionTestUtils;

import com.zawed.api.platform.jobs.JobsHealth.State;
import com.zawed.api.support.IntegrationTest;

/**
 * The job health end to end, over HTTP (ADR 0028): a job with no success in its window turns the
 * public health answer DOWN (503), a failing one WARN (200), while the readiness and liveness probes
 * that Railway and the platform use stay UP. The job states are given (a mock of {@link JobsHealth}),
 * in a context of this class's own, so the shared context's health answer is never touched.
 */
class JobsHealthEndpointTests extends IntegrationTest {

	@MockitoBean
	JobsHealth health;

	@Autowired
	JobsHealthIndicator indicator;

	@BeforeEach
	void forgetTheCachedAnswer() {
		ReflectionTestUtils.setField(indicator, "cached", null);
	}

	@Test
	void anOverdueJobTurnsTheHealthDownButNotTheProbes() throws Exception {
		when(health.statuses()).thenReturn(List.of(status("ledger-reconciliation", State.OVERDUE),
				status("nonce-retention", State.OK)));

		HttpResponse<String> overall = get("/api/v1/health");
		assertThat(overall.statusCode()).isEqualTo(503);
		assertThat(JsonPath.<String>read(overall.body(), "$.status")).isEqualTo("DOWN");
		assertThat(overall.body()).doesNotContain("ledger-reconciliation").doesNotContain("components");

		assertProbesUp();
	}

	@Test
	void aFailingJobTurnsTheHealthWarnWhichStillAnswers200() throws Exception {
		when(health.statuses()).thenReturn(List.of(status("session-retention", State.FAILING),
				status("nonce-retention", State.OK)));

		HttpResponse<String> overall = get("/api/v1/health");
		assertThat(overall.statusCode()).isEqualTo(200);
		assertThat(JsonPath.<String>read(overall.body(), "$.status")).isEqualTo("WARN");

		assertProbesUp();
	}

	@Test
	void healthyAndPendingJobsLeaveTheHealthUp() throws Exception {
		when(health.statuses()).thenReturn(List.of(status("ledger-reconciliation", State.PENDING),
				status("nonce-retention", State.OK)));

		HttpResponse<String> overall = get("/api/v1/health");
		assertThat(overall.statusCode()).isEqualTo(200);
		assertThat(JsonPath.<String>read(overall.body(), "$.status")).isEqualTo("UP");
	}

	private void assertProbesUp() throws Exception {
		HttpResponse<String> readiness = get("/api/v1/health/readiness");
		assertThat(readiness.statusCode()).isEqualTo(200);
		assertThat(JsonPath.<String>read(readiness.body(), "$.status")).isEqualTo("UP");
		HttpResponse<String> liveness = get("/api/v1/health/liveness");
		assertThat(liveness.statusCode()).isEqualTo(200);
		assertThat(JsonPath.<String>read(liveness.body(), "$.status")).isEqualTo("UP");
	}

	private static JobsHealth.Status status(String job, State state) {
		Instant now = Instant.now();
		return new JobsHealth.Status(new Job(job, Duration.ofHours(1)), state, now, null, null, null, null, null, null);
	}

}
