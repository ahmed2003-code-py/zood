package com.zawed.api;

import static org.assertj.core.api.Assertions.assertThat;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Set;
import java.util.TreeSet;
import java.util.stream.Stream;

import org.junit.jupiter.api.Test;

/**
 * Rules the compiler cannot check. Each scope escalation is a place where row-level security is
 * deliberately set aside, so the list of them is closed: a new one fails here until it is reviewed
 * and added (B4 contract §1).
 */
class ArchitectureTests {

	private static final Path SOURCES = Path.of("src", "main", "java", "com", "zawed", "api");

	/** Who may call {@code DbScope.runAs}, and why. */
	private static final Set<String> ESCALATIONS = Set.of(
			// Sign-in: runs as the user it has just identified by password, code or refresh token.
			"identity/Sessions.java", "identity/SignIn.java",
			// The POS channel: no user; platform scope after the activation code or device signature.
			"network/DeviceChannel.java", "network/AgentSessions.java",
			// The fueling engine, for a device and agent the POS chain has verified; the receipt read
			// behind a verified link signature.
			"fueling/FuelingEngine.java", "fueling/Receipts.java");

	@Test
	void scopeEscalationsAreTheReviewedOnesOnly() throws IOException {
		Set<String> callers = new TreeSet<>();
		try (Stream<Path> files = Files.walk(SOURCES)) {
			for (Path file : files.filter((p) -> p.toString().endsWith(".java")).toList()) {
				if (Files.readString(file).contains("DbScope.runAs(")) {
					callers.add(SOURCES.relativize(file).toString().replace('\\', '/'));
				}
			}
		}
		assertThat(callers).as("classes that call DbScope.runAs").isEqualTo(new TreeSet<>(ESCALATIONS));
	}

}
