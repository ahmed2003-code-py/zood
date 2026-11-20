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
			"fueling/FuelingEngine.java", "fueling/Receipts.java",
			// Offline sync (B5): a batch from a device the POS chain has verified; the device's snapshot.
			"fueling/OfflineSync.java", "sync/Snapshots.java",
			// Bulk vehicle import (ADR 0026): the worker has no request; it runs as the platform on behalf of
			// the Ops user who confirmed the job, recorded on the job.
			"fleet/FleetImportWorker.java");

	@Test
	void scopeEscalationsAreTheReviewedOnesOnly() throws IOException {
		Set<String> callers = new TreeSet<>();
		try (Stream<Path> files = Files.walk(SOURCES)) {
			for (Path file : files.filter((p) -> p.toString().endsWith(".java")).toList()) {
				// Any mention of runAs counts (a static import or a method reference too), except the
				// declaration itself.
				if (!file.endsWith(Path.of("platform", "security", "DbScope.java"))
						&& RUN_AS.matcher(stripComments(Files.readString(file))).find()) {
					callers.add(SOURCES.relativize(file).toString().replace('\\', '/'));
				}
			}
		}
		assertThat(callers).as("classes that call DbScope.runAs").isEqualTo(new TreeSet<>(ESCALATIONS));
	}

	private static final java.util.regex.Pattern RUN_AS = java.util.regex.Pattern.compile("\\brunAs\\b");

	/**
	 * Scheduled work that is deliberately not recorded in {@code job_runs} (ADR 0028), and why. Every
	 * other {@code @Scheduled} class must run its body through {@code Jobs.run}, so a job that silently
	 * stops shows in the health check.
	 */
	private static final Set<String> UNRECORDED_SCHEDULES = Set.of(
			// In-process only: sweeps this instance's own rate-limit windows; no database work.
			"platform/ratelimit/InMemoryRateLimiter.java",
			// Polls every 2 seconds, mostly finding nothing (about 43,000 ticks a day). Its progress is recorded
			// on each import job itself (fleet_import_jobs: status, lease, counters), where a stall shows.
			"fleet/FleetImportWorker.java");

	@Test
	void everyScheduledJobIsRecordedOrAReviewedException() throws IOException {
		Set<String> unrecorded = new TreeSet<>();
		try (Stream<Path> files = Files.walk(SOURCES)) {
			for (Path file : files.filter((p) -> p.toString().endsWith(".java")).toList()) {
				String source = stripComments(Files.readString(file));
				if (source.contains("@Scheduled") && !source.contains("jobs.run(")) {
					unrecorded.add(SOURCES.relativize(file).toString().replace('\\', '/'));
				}
			}
		}
		assertThat(unrecorded).as("@Scheduled classes that do not record their runs through Jobs.run")
			.isEqualTo(new TreeSet<>(UNRECORDED_SCHEDULES));
	}

	private static String stripComments(String source) {
		return source.replaceAll("(?s)/\\*.*?\\*/", "").replaceAll("//[^\\n]*", "");
	}

}
