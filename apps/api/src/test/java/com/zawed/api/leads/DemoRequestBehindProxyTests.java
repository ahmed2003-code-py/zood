package com.zawed.api.leads;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.time.Instant;
import java.util.UUID;
import java.util.concurrent.ThreadLocalRandom;

import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.test.context.TestPropertySource;

import com.zawed.api.support.IntegrationTest;
import com.zawed.api.support.Owner;

/**
 * The per-address limit with forwarded headers on, as the {@code deployed} profile runs
 * ({@code server.forward-headers-strategy: native}: Tomcat's RemoteIpValve with its default internal
 * proxies, which include 127.0.0.1, so here the test client plays the platform's proxy).
 *
 * <p>A proxy appends the address it saw to {@code X-Forwarded-For}. The valve reads the list from the
 * right and stops at the first address that is not an internal proxy: that is the client. Whatever a
 * client writes itself sits to the left of it and is ignored. Whether Railway's edge appends (rather
 * than replaces) and connects from an address Tomcat counts as internal must be measured on zawed-dev
 * (u1-leads-contract.md §4).
 */
@TestPropertySource(properties = "server.forward-headers-strategy=native")
class DemoRequestBehindProxyTests extends IntegrationTest {

	private static final String PATH = "/api/v1/public/demo-requests";

	@BeforeEach
	@AfterEach
	void noSharedCounters() {
		Owner.update("delete from rate_limit_counters");
	}

	@Test
	void theAddressIsTheOneTheProxySawNotTheOneTheClientWrote() throws Exception {
		String client = "198.51.100.23";
		for (int i = 0; i < 5; i++) {
			// The client forges a different address each time; the proxy appends the one it saw.
			HttpResponse<String> response = send("203.0.113." + (i + 1) + ", " + client);
			assertThat(response.statusCode()).as(response.body()).isEqualTo(202);
		}
		assertThat(send("192.0.2.77, " + client).statusCode()).as("the forged addresses never gave a fresh budget")
			.isEqualTo(429);
	}

	@Test
	void forwardedHeadersAreReallyReadHere() throws Exception {
		// The positive control: two clients behind the proxy have two budgets, so the test above could fail.
		for (int i = 0; i < 5; i++) {
			assertThat(send("198.51.100.31").statusCode()).isEqualTo(202);
		}
		assertThat(send("198.51.100.31").statusCode()).isEqualTo(429);
		assertThat(send("198.51.100.32").statusCode()).as("another client").isEqualTo(202);
	}

	private HttpResponse<String> send(String forwardedFor) throws Exception {
		String token = UUID.randomUUID().toString().substring(0, 8);
		String body = "{\"companyName\":\"Proxy Co " + token + "\",\"contactName\":\"Proxy " + token + "\",\"phone\":\"012"
				+ String.format("%08d", ThreadLocalRandom.current().nextInt(100_000_000)) + "\",\"email\":\"proxy-" + token
				+ "@example.test\",\"fleetSize\":\"1-19\",\"locale\":\"en\",\"website\":\"\",\"startedAt\":"
				+ Instant.now().minusSeconds(10).toEpochMilli() + "}";
		return send(request(PATH).header("Content-Type", "application/json")
			.header("X-Forwarded-For", forwardedFor)
			.POST(HttpRequest.BodyPublishers.ofString(body)));
	}

}
