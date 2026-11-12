package com.zawed.api;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;

import org.junit.jupiter.api.Test;
import org.springframework.test.context.TestPropertySource;

import com.zawed.api.support.IntegrationTest;

/**
 * Keeps {@code openapi/openapi.json} equal to what the running API describes. The web client's
 * TypeScript types are generated from that file, so an API change that is not reflected there
 * fails here before it can reach a browser.
 *
 * <p>After an intended API change, refresh the file and the web types:
 *
 * <pre>
 * ./mvnw test -Dtest=OpenApiSnapshotTests -Dzawed.openapi.write=true
 * cd ../web &amp;&amp; npm run api:types
 * </pre>
 */
@TestPropertySource(properties = "springdoc.writer-with-default-pretty-printer=true")
class OpenApiSnapshotTests extends IntegrationTest {

	private static final Path SNAPSHOT = Path.of("openapi", "openapi.json");

	@Test
	void theCommittedDescriptionMatchesTheApi() throws Exception {
		HttpResponse<String> response = get("/api/v1/openapi.json");
		assertThat(response.statusCode()).isEqualTo(200);
		String current = response.body().replace("\r\n", "\n").strip() + "\n";

		if (Boolean.getBoolean("zawed.openapi.write")) {
			Files.createDirectories(SNAPSHOT.getParent());
			Files.writeString(SNAPSHOT, current, StandardCharsets.UTF_8);
			return;
		}

		assertThat(SNAPSHOT).as("run with -Dzawed.openapi.write=true to create it").exists();
		String committed = Files.readString(SNAPSHOT, StandardCharsets.UTF_8).replace("\r\n", "\n");
		assertThat(current).as("openapi/openapi.json is out of date; see this test's Javadoc").isEqualTo(committed);
	}

}
