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
			"fleet/FleetImportWorker.java",
			// Data-key rotation (ADR 0027): no request and no user; re-encrypts the registered encrypted
			// columns of every tenant under the current key, and writes nothing else.
			"platform/crypto/DataKeyRotation.java");

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

	/**
	 * Notifications are written only by the notifications module, from the producers' events (B7 W5):
	 * a producer announces what happened and never decides who is told. The database lets any scope
	 * address the platform, so this is the rule that keeps that door to one writer.
	 */
	@Test
	void onlyTheNotificationsModuleWritesNotifications() throws IOException {
		Set<String> writers = new TreeSet<>();
		try (Stream<Path> files = Files.walk(SOURCES)) {
			for (Path file : files.filter((p) -> p.toString().endsWith(".java")).toList()) {
				if (WRITES_NOTIFICATIONS.matcher(stripComments(Files.readString(file))).find()) {
					writers.add(SOURCES.relativize(file).toString().replace('\\', '/'));
				}
			}
		}
		assertThat(writers).as("classes that write notifications or read state")
			.containsExactlyInAnyOrder("notifications/Notifications.java", "notifications/NotificationRetention.java");
	}

	/**
	 * Any write to {@code notifications}, {@code notification_reads} or {@code notification_read_marks}
	 * (B7 W5 review, L1): an insert, update, delete, merge, upsert target, copy or truncate, with or
	 * without the schema or quotes, and the retention purge function. The database refuses only a
	 * user-less platform session's other types; a signed-in platform user may still address anyone,
	 * so this stays the rule that keeps the writer to one module.
	 */
	private static final java.util.regex.Pattern WRITES_NOTIFICATIONS = java.util.regex.Pattern
		.compile("(?i)(\\b(insert\\s+into|update|delete\\s+from|merge\\s+into|copy|truncate(\\s+table)?)"
				+ "\\s+(only\\s+)?(\"?public\"?\\s*\\.\\s*)?\"?notification|\\bpurge_notifications\\b)");

	/**
	 * Who reads the request path as a string (B7 "path matching"). The raw URI is not the path Spring
	 * Security and Spring MVC route, so a decision keyed on it (a limit, an allow-list) can be walked
	 * around by spelling the path another way. Decisions match with {@code PathPatternRequestMatcher};
	 * the raw URI is read only where that is the point.
	 */
	private static final Set<String> RAW_PATH_READERS = Set.of(
			// The check that the raw path is canonical, before anything else sees it.
			"platform/web/CanonicalPathFilter.java",
			// A log line of an unexpected failure; decides nothing.
			"platform/web/GlobalExceptionHandler.java");

	private static final java.util.regex.Pattern READS_RAW_PATH = java.util.regex.Pattern
		.compile("\\b(getRequestURI|getRequestURL|getServletPath|getPathInfo|getPathTranslated|getContextPath)\\s*\\("
				+ "|::(getRequestURI|getRequestURL|getServletPath|getPathInfo)\\b|\\bUrlPathHelper\\b|\\bServletRequestPathUtils\\b"
				+ "|\\b(FORWARD|INCLUDE|ERROR)_REQUEST_URI\\b");

	@Test
	void onlyTheReviewedClassesReadTheRequestPathAsAString() throws IOException {
		Set<String> readers = new TreeSet<>();
		try (Stream<Path> files = Files.walk(SOURCES)) {
			for (Path file : files.filter((p) -> p.toString().endsWith(".java")).toList()) {
				if (READS_RAW_PATH.matcher(stripComments(Files.readString(file))).find()) {
					readers.add(SOURCES.relativize(file).toString().replace('\\', '/'));
				}
			}
		}
		assertThat(readers).as("classes that read the request path as a string").isEqualTo(new TreeSet<>(RAW_PATH_READERS));
	}

	private static String stripComments(String source) {
		return source.replaceAll("(?s)/\\*.*?\\*/", "").replaceAll("//[^\\n]*", "");
	}

}
