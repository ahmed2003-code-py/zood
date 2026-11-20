package com.zawed.api.leads;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import org.junit.jupiter.api.Test;
import org.springframework.mock.web.MockHttpServletRequest;
import org.springframework.mock.web.MockHttpServletResponse;
import org.springframework.web.method.HandlerMethod;

import com.zawed.api.platform.web.ApiException;

/** Which spellings of the path meet the gate, and the handler-side check behind it (review of U1). */
class DemoRequestGateTests {

	@Test
	void theGateMatchesThePathAsSpringDoesOnDecodedSegments() {
		for (String path : new String[] { "/api/v1/public/demo-requests", "/api/v1/public/demo%2Drequests",
				"/api/v1/public/demo%2drequests", "/api/v1/%70ublic/demo-request%73", "/api/v1/public/demo-requests%" }) {
			assertThat(DemoRequestGate.isDemoPath(post(path))).as(path).isTrue();
		}
		for (String path : new String[] { "/api/v1/public/demo-request", "/api/v1/ops/leads", "/api/v1/public/demo%20requests" }) {
			assertThat(DemoRequestGate.isDemoPath(post(path))).as(path).isFalse();
		}
	}

	@Test
	void theHandlerRunsOnlyForARequestTheGateLetThrough() throws Exception {
		HandlerMethod demo = new HandlerMethod(new LeadsController(null),
				LeadsController.class.getDeclaredMethod("requestDemo", LeadsController.DemoRequest.class));
		LeadsWebConfig.GateRequired check = new LeadsWebConfig.GateRequired();

		MockHttpServletRequest ungated = post("/api/v1/public/demo-requests");
		assertThatThrownBy(() -> check.preHandle(ungated, new MockHttpServletResponse(), demo))
			.isInstanceOfSatisfying(ApiException.class, (ex) -> assertThat(ex.status().value()).isEqualTo(400));

		MockHttpServletRequest gated = post("/api/v1/public/demo-requests");
		gated.setAttribute(DemoRequestGate.PASSED, Boolean.TRUE);
		assertThat(check.preHandle(gated, new MockHttpServletResponse(), demo)).isTrue();

		HandlerMethod list = new HandlerMethod(new LeadsController(null), LeadsController.class.getDeclaredMethod("list",
				Leads.Status.class, String.class, Integer.class));
		assertThat(check.preHandle(post("/api/v1/ops/leads"), new MockHttpServletResponse(), list)).isTrue();
	}

	private static MockHttpServletRequest post(String uri) {
		MockHttpServletRequest request = new MockHttpServletRequest("POST", uri);
		request.setRequestURI(uri);
		return request;
	}

}
