package com.zawed.api.support;

import java.io.IOException;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;

import org.springframework.beans.factory.annotation.Value;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.boot.test.context.SpringBootTest.WebEnvironment;
import org.springframework.test.context.DynamicPropertyRegistry;
import org.springframework.test.context.DynamicPropertySource;

/**
 * Base for tests that run the whole application: a real HTTP port, the real filter chain and a real
 * PostgreSQL with the Flyway migrations applied. Requests go over the network on purpose, so what
 * is asserted is what a client receives.
 */
@SpringBootTest(webEnvironment = WebEnvironment.RANDOM_PORT, properties = "zawed.environment=test")
public abstract class IntegrationTest {

	private static final HttpClient HTTP = HttpClient.newHttpClient();

	@Value("${local.server.port}")
	private int port;

	@DynamicPropertySource
	static void database(DynamicPropertyRegistry registry) {
		registry.add("spring.datasource.url", TestDatabase::jdbcUrl);
		registry.add("spring.datasource.username", () -> TestDatabase.USERNAME);
		registry.add("spring.datasource.password", () -> TestDatabase.PASSWORD);
	}

	protected HttpRequest.Builder request(String path) {
		return HttpRequest.newBuilder(URI.create("http://127.0.0.1:" + port + path));
	}

	protected HttpResponse<String> get(String path) throws IOException, InterruptedException {
		return send(request(path).GET());
	}

	protected HttpResponse<String> send(HttpRequest.Builder request) throws IOException, InterruptedException {
		return HTTP.send(request.build(), HttpResponse.BodyHandlers.ofString());
	}

}
