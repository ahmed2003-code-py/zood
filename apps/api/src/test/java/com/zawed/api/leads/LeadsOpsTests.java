package com.zawed.api.leads;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.sql.Timestamp;
import java.time.Instant;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ThreadLocalRandom;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;

import com.zawed.api.identity.IdentityTestSupport;
import com.zawed.api.platform.ids.UuidV7;
import com.zawed.api.platform.security.Role;
import com.zawed.api.support.Owner;

/**
 * The Ops side of the leads (u1-leads-contract.md §4): the list, newest first with keyset paging on
 * (created_at, id) and a status filter, and the update, audited in the same transaction with the
 * reference and statuses only.
 */
class LeadsOpsTests extends IdentityTestSupport {

	private SignedIn ops;

	@BeforeEach
	void signInOps() throws Exception {
		ops = signIn(seed(Role.OPS));
	}

	@Test
	void theListIsNewestFirstAndPagesOnCreatedAtThenId() throws Exception {
		// Far in the future, so nothing another test stores comes before them. Two share a creation time:
		// the id decides between them.
		Instant base = Instant.parse("2199-01-01T00:00:00Z");
		record Planted(UUID id, Instant createdAt) {
		}
		List<Planted> planted = new ArrayList<>();
		for (int i = 0; i < 5; i++) {
			Instant at = base.plusSeconds(i == 4 ? 3 : i);
			planted.add(new Planted(plant(at, "NEW"), at));
		}
		List<String> expected = planted.stream()
			// PostgreSQL orders uuids byte by byte, which is the order of their text.
			.sorted(Comparator.comparing(Planted::createdAt).thenComparing((Planted p) -> p.id().toString()).reversed())
			.map((p) -> p.id().toString())
			.toList();

		List<String> seen = new ArrayList<>();
		String cursor = null;
		for (int page = 0; page < 3; page++) {
			HttpResponse<String> response = authed("GET",
					"/api/v1/ops/leads?limit=2" + (cursor == null ? "" : "&cursor=" + cursor), ops.accessToken(), null);
			assertThat(response.statusCode()).as(response.body()).isEqualTo(200);
			seen.addAll(JsonPath.read(response.body(), "$.items[*].id"));
			cursor = JsonPath.read(response.body(), "$.nextCursor");
			if (page < 2) {
				assertThat(cursor).as("page %d of at least three", page + 1).isNotNull();
			}
		}
		assertThat(seen.subList(0, 5)).isEqualTo(expected);
		assertThat(seen).doesNotHaveDuplicates();
	}

	@Test
	void theListFiltersByStatusAndShowsTheWholeLead() throws Exception {
		UUID qualified = plant(Instant.parse("2198-01-01T00:00:00Z"), "QUALIFIED");
		plant(Instant.parse("2198-01-01T00:00:01Z"), "NEW");

		HttpResponse<String> response = authed("GET", "/api/v1/ops/leads?status=QUALIFIED", ops.accessToken(), null);
		assertThat(response.statusCode()).as(response.body()).isEqualTo(200);
		List<String> statuses = JsonPath.read(response.body(), "$.items[*].status");
		assertThat(statuses).isNotEmpty().containsOnly("QUALIFIED");
		Map<String, Object> first = JsonPath.read(response.body(), "$.items[0]");
		assertThat(first).containsOnlyKeys("id", "reference", "companyName", "contactName", "phone", "email", "fleetSize",
				"locale", "status", "note", "createdAt", "updatedAt", "requestCount", "lastRequestedAt", "differingRequests");
		assertThat(first.get("requestCount")).isEqualTo(1);
		assertThat(first.get("differingRequests")).isEqualTo(0);
		assertThat(first.get("id")).isEqualTo(qualified.toString());
		assertThat((String) first.get("reference")).matches("L-[0-9]{5,}");
		assertThat(first.get("fleetSize")).isEqualTo("50-99");
		assertThat(first.get("locale")).isEqualTo("en");

		// limit: the bounds of every Ops list (1–100, 25 by default).
		HttpResponse<String> one = authed("GET", "/api/v1/ops/leads?status=QUALIFIED&limit=1", ops.accessToken(), null);
		assertThat(JsonPath.<List<String>>read(one.body(), "$.items[*].id")).containsExactly(qualified.toString());
		HttpResponse<String> clamped = authed("GET", "/api/v1/ops/leads?limit=0", ops.accessToken(), null);
		assertThat(clamped.statusCode()).isEqualTo(200);
		assertThat(JsonPath.<List<Object>>read(clamped.body(), "$.items")).hasSize(1);
		HttpResponse<String> large = authed("GET", "/api/v1/ops/leads?limit=500", ops.accessToken(), null);
		assertThat(JsonPath.<List<Object>>read(large.body(), "$.items").size()).isLessThanOrEqualTo(100);

		assertThat(authed("GET", "/api/v1/ops/leads?status=WON", ops.accessToken(), null).statusCode()).isEqualTo(400);
		HttpResponse<String> badCursor = authed("GET", "/api/v1/ops/leads?cursor=abc", ops.accessToken(), null);
		assertThat(badCursor.statusCode()).isEqualTo(400);
		assertThat(code(badCursor)).isEqualTo("INVALID_CURSOR");
	}

	@Test
	void anUpdateChangesTheStatusAndNoteAndIsAuditedWithoutPersonalData() throws Exception {
		UUID id = plant(Instant.now().minusSeconds(60), "NEW");
		String note = "Called.\nCall back on Sunday about pricing.";

		HttpResponse<String> contacted = patch(id, "{\"status\":\"CONTACTED\",\"note\":" + quote(note) + "}");
		assertThat(contacted.statusCode()).as(contacted.body()).isEqualTo(200);
		assertThat(JsonPath.<String>read(contacted.body(), "$.status")).isEqualTo("CONTACTED");
		assertThat(JsonPath.<String>read(contacted.body(), "$.note")).isEqualTo(note);
		assertThat(Instant.parse(JsonPath.read(contacted.body(), "$.updatedAt")))
			.isAfter(Instant.parse(JsonPath.read(contacted.body(), "$.createdAt")));
		assertThat(Owner.<UUID>queryOne("select updated_by from leads where id = ?", id)).isEqualTo(ops.userId());

		String metadata = Owner.queryOne("""
				select metadata::text from audit_log
				where action = 'LEAD_UPDATED' and entity_type = 'LEAD' and entity_id = ? and actor_id = ?
				  and scope_type = 'PLATFORM'""", id, ops.userId());
		String reference = Owner.queryOne("select reference from leads where id = ?", id);
		assertThat(JsonPath.<Map<String, Object>>read(metadata, "$")).containsExactlyInAnyOrderEntriesOf(
				Map.of("reference", reference, "from", "NEW", "to", "CONTACTED", "noteChanged", true));
		for (String personal : List.of("company_name", "contact_name", "phone", "email")) {
			String value = Owner.queryOne("select " + personal + " from leads where id = ?", id);
			assertThat(metadata).doesNotContain(value);
		}
		assertThat(metadata).doesNotContain("Sunday");

		// Closing without a note keeps the note and starts the retention clock.
		HttpResponse<String> closed = patch(id, "{\"status\":\"CLOSED\"}");
		assertThat(JsonPath.<String>read(closed.body(), "$.note")).isEqualTo(note);
		assertThat(Owner.<Timestamp>queryOne("select closed_at from leads where id = ?", id)).isNotNull();

		// Reopening with a blank note removes it and stops the clock.
		HttpResponse<String> reopened = patch(id, "{\"status\":\"NEW\",\"note\":\"  \"}");
		assertThat(reopened.statusCode()).isEqualTo(200);
		assertThat(JsonPath.<Object>read(reopened.body(), "$.note")).isNull();
		assertThat(Owner.<Timestamp>queryOne("select closed_at from leads where id = ?", id)).isNull();
		// The web clears a note with an empty string: stored as null.
		patch(id, "{\"status\":\"NEW\",\"note\":\"again\"}");
		HttpResponse<String> cleared = patch(id, "{\"status\":\"NEW\",\"note\":\"\"}");
		assertThat(cleared.statusCode()).isEqualTo(200);
		assertThat(JsonPath.<Object>read(cleared.body(), "$.note")).isNull();
		assertThat(Owner.<String>queryOne("select note from leads where id = ?", id)).isNull();
		assertThat(Owner.<Long>queryOne("select count(*) from audit_log where action = 'LEAD_UPDATED' and entity_id = ?",
				id)).isEqualTo(5L);
	}

	@Test
	void anInvalidUpdateIsRefusedAndAnUnknownLeadIs404() throws Exception {
		UUID id = plant(Instant.now().minusSeconds(60), "NEW");
		assertThat(fieldCode(patch(id, "{\"status\":\"CONTACTED\",\"note\":" + quote("x".repeat(1001)) + "}"), "note"))
			.isEqualTo("SIZE");
		assertThat(fieldCode(patch(id, "{\"status\":\"CONTACTED\",\"note\":\"bell\\u0007\"}"), "note")).isEqualTo("PATTERN");
		assertThat(fieldCode(patch(id, "{\"note\":\"x\"}"), "status")).isEqualTo("NOT_NULL");
		assertThat(patch(id, "{\"status\":\"WON\"}").statusCode()).isEqualTo(400);
		assertThat(Owner.<String>queryOne("select status from leads where id = ?", id)).isEqualTo("NEW");

		HttpResponse<String> missing = patch(UUID.randomUUID(), "{\"status\":\"CONTACTED\"}");
		assertThat(missing.statusCode()).isEqualTo(404);
		assertThat(code(missing)).isEqualTo("NOT_FOUND");
	}

	// ------------------------------------------------------------------------------------ helpers

	/** A lead written as the owner, with its creation time chosen by the test. */
	static UUID plant(Instant createdAt, String status) {
		UUID id = UuidV7.create(createdAt.toEpochMilli(), ThreadLocalRandom.current());
		String token = UUID.randomUUID().toString().substring(0, 8);
		Owner.update("""
				insert into leads (id, reference, company_name, contact_name, phone, email, fleet_size, locale, status,
				                   request_count, last_requested_at, created_at, updated_at, closed_at)
				values (?, 'L-' || nextval('lead_reference_seq'), ?, ?, ?, ?, '50-99', 'en', ?, 1, ?, ?, ?,
				        case when ? = 'CLOSED' then cast(? as timestamptz) end)
				""", id, "Planted Co " + token, "Planted " + token,
				"010" + String.format("%08d", ThreadLocalRandom.current().nextInt(100_000_000)), "p-" + token + "@example.test",
				status, Timestamp.from(createdAt), Timestamp.from(createdAt), Timestamp.from(createdAt), status,
				Timestamp.from(createdAt));
		return id;
	}

	private HttpResponse<String> patch(UUID id, String body) throws Exception {
		return authed("PATCH", "/api/v1/ops/leads/" + id, ops.accessToken(), body);
	}

	private static String fieldCode(HttpResponse<String> response, String field) {
		assertThat(response.statusCode()).as(response.body()).isEqualTo(400);
		assertThat(code(response)).isEqualTo("VALIDATION_FAILED");
		List<String> codes = JsonPath.read(response.body(), "$.fields[?(@.field == '" + field + "')].code");
		return codes.isEmpty() ? null : codes.getFirst();
	}

	private static String quote(String text) {
		return "\"" + text.replace("\\", "\\\\").replace("\"", "\\\"").replace("\n", "\\n") + "\"";
	}

}
