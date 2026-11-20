package com.zawed.api.leads;

import static org.assertj.core.api.Assertions.assertThat;

import java.io.ByteArrayInputStream;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.time.Instant;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ThreadLocalRandom;

import com.jayway.jsonpath.JsonPath;

import io.micrometer.core.instrument.Counter;
import io.micrometer.core.instrument.MeterRegistry;
import io.micrometer.core.instrument.composite.CompositeMeterRegistry;
import io.micrometer.core.instrument.simple.SimpleMeterRegistry;

import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.extension.ExtendWith;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.system.CapturedOutput;
import org.springframework.boot.test.system.OutputCaptureExtension;

import tools.jackson.databind.ObjectMapper;

import com.zawed.api.identity.IdentityTestSupport;
import com.zawed.api.platform.crypto.LookupHasher;
import com.zawed.api.platform.ratelimit.InMemoryRateLimiter;
import com.zawed.api.support.Concurrently;
import com.zawed.api.support.Owner;

/**
 * The public demo-request form (u1-leads-contract.md), over HTTP against the real chain and database:
 * validation and normalisation, the honeypot and the time trap (202 and no row), repeats, the body
 * cap, the shared per-address and per-contact limits, and no personal data in the log.
 *
 * <p>Every request here comes from 127.0.0.1, so each test starts and ends with no counters.
 */
@ExtendWith(OutputCaptureExtension.class)
class DemoRequestTests extends IdentityTestSupport {

	private static final String PATH = "/api/v1/public/demo-requests";

	@Autowired
	ObjectMapper json;

	@Autowired
	MeterRegistry metrics;

	@Autowired
	InMemoryRateLimiter memoryLimiter;

	@Autowired
	LookupHasher hasher;

	@BeforeEach
	@AfterEach
	void noSharedCounters() {
		Owner.update("delete from rate_limit_counters");
	}

	@BeforeEach
	void countMetricsInMemory() {
		// Tests switch metric export off; an in-memory registry under the composite still counts.
		if (metrics instanceof CompositeMeterRegistry composite && composite.getRegistries().isEmpty()) {
			composite.add(new SimpleMeterRegistry());
		}
	}

	// ------------------------------------------------------------------------------ the happy path

	@Test
	void aValidRequestBecomesANewLeadWithCleanedValues() throws Exception {
		String digits = digits();
		String token = token();
		Map<String, Object> form = form();
		form.put("companyName", "  Nile \t Freight\n" + token + "  ");
		form.put("contactName", " Mona   Adel ");
		form.put("phone", "+20 10" + digits.substring(0, 2) + "-" + digits.substring(2, 5) + " " + digits.substring(5));
		form.put("email", "  Mona.Adel+" + token + "@Example.TEST ");
		double created = counted("created");

		HttpResponse<String> response = submit(form);

		assertThat(response.statusCode()).as(response.body()).isEqualTo(202);
		assertThat(response.body()).isEqualTo("{\"status\":\"RECEIVED\"}");
		String email = "mona.adel+" + token + "@example.test";
		assertThat(Owner.<String>queryOne("select phone from leads where email = ?", email)).isEqualTo("010" + digits);
		assertThat(Owner.<String>queryOne("select company_name from leads where email = ?", email))
			.isEqualTo("Nile Freight " + token);
		assertThat(Owner.<String>queryOne("select contact_name from leads where email = ?", email)).isEqualTo("Mona Adel");
		assertThat(Owner.<String>queryOne("select reference from leads where email = ?", email)).matches("L-[0-9]{5,}");
		assertThat(Owner.<String>queryOne("select status || ' ' || request_count || ' ' || fleet_size || ' ' || locale "
				+ "from leads where email = ?", email)).isEqualTo("NEW 1 20-49 ar");
		assertThat(counted("created")).isEqualTo(created + 1);
	}

	@Test
	void arabicDigitsAndTheInternationalPrefixAreTheSameNumber() throws Exception {
		Map<String, Object> form = form();
		String digits = digits().substring(1);
		form.put("phone", arabic("0020 100 " + digits.substring(0, 3) + " " + digits.substring(3)));
		HttpResponse<String> response = submit(form);
		assertThat(response.statusCode()).as(response.body()).isEqualTo(202);
		assertThat(Owner.<String>queryOne("select phone from leads where email = ?", form.get("email")))
			.isEqualTo("0100" + digits);
	}

	// ------------------------------------------------------------------------------- validation

	@Test
	void invalidFieldsAre400WithTheFieldAndAStableCodeAndStoreNothing() throws Exception {
		Object[][] cases = { { "companyName", "A", "SIZE" }, { "companyName", "Nile\u0007Freight", "PATTERN" },
				{ "companyName", "x".repeat(121), "SIZE" }, { "companyName", null, "NOT_BLANK" },
				{ "contactName", "Mona\u202EAdel", "PATTERN" }, { "contactName", "   ", "NOT_BLANK" },
				{ "phone", "01301234567", "PATTERN" }, { "phone", "0100123456", "PATTERN" },
				{ "phone", "+1 202 555 0100", "PATTERN" }, { "phone", null, "NOT_BLANK" },
				{ "email", "not-an-email", "EMAIL" }, { "email", "mona\u3000x@example.com", "EMAIL" },
				{ "email", "mona@exa\u00A0mple.com", "EMAIL" }, { "email", "mona\u200B@example.com", "EMAIL" },
				{ "companyName", "Nile\u200BFreight", "PATTERN" }, { "contactName", "Mona\u200FAdel", "PATTERN" },
				{ "contactName", "Mona\uFEFF", "PATTERN" }, { "companyName", "Nile\uE000Co", "PATTERN" }, { "email", "mona@localhost", "EMAIL" },
				{ "email", "m".repeat(250) + "@x.eg", "SIZE" }, { "fleetSize", "10-20", "PATTERN" },
				{ "fleetSize", null, "NOT_NULL" }, { "locale", "fr", "PATTERN" }, { "startedAt", null, "NOT_NULL" } };
		for (Object[] c : cases) {
			noSharedCounters(); // each refusal also spends the address's budget
			Map<String, Object> form = form();
			form.put((String) c[0], c[1]);
			HttpResponse<String> response = submit(form);
			assertThat(response.statusCode()).as("%s = %s: %s", c[0], c[1], response.body()).isEqualTo(400);
			assertThat(code(response)).isEqualTo("VALIDATION_FAILED");
			List<String> codes = JsonPath.read(response.body(), "$.fields[?(@.field == '" + c[0] + "')].code");
			assertThat(codes).as("%s = %s: %s", c[0], c[1], response.body()).contains((String) c[2]);
			assertThat(Owner.<Long>queryOne("select count(*) from leads where email = ? or phone = ?",
					String.valueOf(form.get("email")).toLowerCase(), String.valueOf(form.get("phone"))))
				.isZero();
		}
	}

	@Test
	void anUnknownFieldOrABrokenBodyIsRefused() throws Exception {
		Map<String, Object> form = form();
		form.put("source", "campaign");
		assertThat(submit(form).statusCode()).isEqualTo(400);
		assertThat(send(PATH, "{\"companyName\":").statusCode()).isEqualTo(400);
		assertThat(Owner.<Long>queryOne("select count(*) from leads where email = ?", form.get("email"))).isZero();
	}

	@Test
	void aBodyOver4KiBIsRefusedBeforeItIsParsedWithOrWithoutALength() throws Exception {
		Map<String, Object> form = form();
		form.put("website", "");
		form.put("companyName", "Nile " + "x".repeat(4200));
		HttpResponse<String> sized = submit(form);
		assertThat(sized.statusCode()).as(sized.body()).isEqualTo(413);
		assertThat(code(sized)).isEqualTo("PAYLOAD_TOO_LARGE");

		// Chunked: no Content-Length to trust; the gate stops reading after the cap.
		byte[] body = json.writeValueAsString(form).getBytes(java.nio.charset.StandardCharsets.UTF_8);
		HttpResponse<String> chunked = send(request(PATH).header("Content-Type", "application/json")
			.POST(HttpRequest.BodyPublishers.ofInputStream(() -> new ByteArrayInputStream(body))));
		assertThat(chunked.statusCode()).as(chunked.body()).isEqualTo(413);
		assertThat(Owner.<Long>queryOne("select count(*) from leads where email = ?", form.get("email"))).isZero();

		// Just under the cap is read and judged on its fields (here: too long a name).
		form.put("companyName", "x".repeat(3500));
		assertThat(submit(form).statusCode()).isEqualTo(400);
	}

	// ----------------------------------------------------------------- honeypot and time trap

	@Test
	void aFilledHiddenFieldOrABotsTimingIsAnswered202AndNothingIsStored() throws Exception {
		long now = Instant.now().toEpochMilli();
		Object[][] cases = { { "website", "https://spam.example", "honeypot" }, { "startedAt", now - 1_000, "too_fast" },
				{ "startedAt", now, "too_fast" }, { "startedAt", now + 60_000, "future" },
				{ "startedAt", now - 25L * 3600 * 1000, "stale" } };
		for (Object[] c : cases) {
			noSharedCounters();
			Map<String, Object> form = form();
			form.put((String) c[0], c[1]);
			double before = counted((String) c[2]);
			HttpResponse<String> response = submit(form);
			assertThat(response.statusCode()).as("%s = %s", c[0], c[1]).isEqualTo(202);
			assertThat(response.body()).isEqualTo("{\"status\":\"RECEIVED\"}");
			assertThat(Owner.<Long>queryOne("select count(*) from leads where email = ? or phone = ?", form.get("email"),
					form.get("phone")))
				.as("%s = %s", c[0], c[1])
				.isZero();
			assertThat(counted((String) c[2])).as("metric for %s", c[2]).isEqualTo(before + 1);
		}
	}

	// ----------------------------------------------------------------------------------- repeats

	@Test
	void aRepeatByPhoneOrEmailWithin24HoursCountsOnTheSameLead() throws Exception {
		Map<String, Object> first = form();
		HttpResponse<String> created = submit(first);
		String phone = (String) first.get("phone");
		String email = (String) first.get("email");

		Map<String, Object> samePhone = form();
		samePhone.put("phone", phone);
		samePhone.put("companyName", "Another Name " + token());
		Map<String, Object> sameEmail = form();
		sameEmail.put("email", email.toUpperCase());
		HttpResponse<String> second = submit(samePhone);
		HttpResponse<String> third = submit(sameEmail);

		// The same answer whether the request made a lead or not: no existence oracle.
		assertThat(List.of(created, second, third)).allSatisfy((r) -> {
			assertThat(r.statusCode()).isEqualTo(202);
			assertThat(r.body()).isEqualTo(created.body());
		});
		String lead = "select %s from leads where phone = ? or email = ?";
		assertThat(Owner.<Long>queryOne(lead.formatted("count(*)"), phone, email)).isEqualTo(1L);
		assertThat(Owner.<Integer>queryOne(lead.formatted("request_count"), phone, email)).isEqualTo(3);
		assertThat(Owner.<String>queryOne(lead.formatted("company_name"), phone, email))
			.isEqualTo(first.get("companyName"));
		assertThat(Owner.<Boolean>queryOne(lead.formatted("last_requested_at > created_at"), phone, email)).isTrue();

		// A day later, the same phone is a new request.
		Owner.update("update leads set last_requested_at = last_requested_at - interval '25 hours' where phone = ?", phone);
		Map<String, Object> later = form();
		later.put("phone", phone);
		assertThat(submit(later).statusCode()).isEqualTo(202);
		assertThat(Owner.<Long>queryOne("select count(*) from leads where phone = ?", phone)).isEqualTo(2L);
	}

	@Test
	void twoIdenticalRequestsAtTheSameMomentMakeOneLead() throws Exception {
		Map<String, Object> form = form();
		List<HttpResponse<String>> responses = Concurrently.run(2, (i) -> submit(form));
		assertThat(responses).allSatisfy((r) -> assertThat(r.statusCode()).isEqualTo(202));
		assertThat(Owner.<Long>queryOne("select count(*) from leads where email = ?", form.get("email"))).isEqualTo(1L);
		assertThat(Owner.<Integer>queryOne("select request_count from leads where email = ?", form.get("email")))
			.isEqualTo(2);
	}

	// ------------------------------------------------------------------------------ rate limits

	@Test
	void anAddressGetsFiveAnHourAndTwentyADayEvenAfterARestart() throws Exception {
		double limited = counted("rate_limited_address");
		for (int i = 0; i < 5; i++) {
			HttpResponse<String> response = submit(form());
			assertThat(response.statusCode()).as(response.body()).isEqualTo(202);
		}
		// A restart wipes in-memory counters; these live in the database.
		memoryLimiter.reset();
		Map<String, Object> sixth = form();
		HttpResponse<String> refused = submit(sixth);
		assertThat(refused.statusCode()).isEqualTo(429);
		assertThat(code(refused)).isEqualTo("RATE_LIMITED");
		long retryAfter = Long.parseLong(refused.headers().firstValue("Retry-After").orElseThrow());
		assertThat(retryAfter).isBetween(1L, 3600L);
		assertThat(JsonPath.<Integer>read(refused.body(), "$.retryAfterSeconds")).isEqualTo((int) retryAfter);
		assertThat(Owner.<Long>queryOne("select count(*) from leads where email = ?", sixth.get("email"))).isZero();
		assertThat(counted("rate_limited_address")).isEqualTo(limited + 1);

		// An hour later the hourly budget is back, and the day's still counts: 20 a day.
		Owner.update("""
				update rate_limit_counters set window_start = window_start - interval '1 hour',
				       expires_at = expires_at - interval '1 hour' where bucket = 'demo-ip-hour'""");
		assertThat(submit(form()).statusCode()).isEqualTo(202);
		Owner.update("update rate_limit_counters set hits = 20 where bucket = 'demo-ip-day'");
		HttpResponse<String> day = submit(form());
		assertThat(day.statusCode()).isEqualTo(429);
		assertThat(Long.parseLong(day.headers().firstValue("Retry-After").orElseThrow())).isBetween(1L, 86_400L);
	}

	@Test
	void anEncodedSpellingOfThePathNeverReachesTheFormWithoutTheGate() throws Exception {
		// Spring matches decoded segments, so demo%2Drequests would be the same route (review of U1). The
		// B7 canonical-path filter refuses it before anything else; it never stores a lead or passes the cap.
		Map<String, Object> large = form();
		large.put("companyName", "x".repeat(5 * 1024));
		for (String path : new String[] { "/api/v1/public/demo%2Drequests", "/api/v1/public/demo%2drequest%73" }) {
			HttpResponse<String> tooLarge = post(path, json.writeValueAsString(large));
			assertThat(tooLarge.statusCode()).as(tooLarge.body()).isEqualTo(400);
			assertThat(code(tooLarge)).isEqualTo("PATH_NOT_CANONICAL");
		}
		for (int i = 0; i < 6; i++) {
			Map<String, Object> form = form();
			HttpResponse<String> response = post("/api/v1/public/demo%2Drequests", json.writeValueAsString(form));
			assertThat(response.statusCode()).isEqualTo(400);
			assertThat(Owner.<Long>queryOne("select count(*) from leads where email = ?", form.get("email"))).isZero();
		}
	}

	@Test
	void refusedAndSetAsideRequestsSpendTheAddressBudgetToo() throws Exception {
		for (int i = 0; i < 5; i++) {
			Map<String, Object> form = form();
			form.put(i % 2 == 0 ? "phone" : "website", i % 2 == 0 ? "bad" : "spam");
			assertThat(submit(form).statusCode()).isIn(400, 202);
		}
		assertThat(submit(form()).statusCode()).isEqualTo(429);
		assertThat(send(PATH, "not json").statusCode()).isEqualTo(429);
	}

	@Test
	void pastThreeADayAPhoneIsSetAsideSilentlyHoweverItIsWritten() throws Exception {
		String digits = digits();
		String[] spellings = { "010" + digits, "+20 10 " + digits, "0020-10-" + digits, arabic("010" + digits) };
		double limited = counted("rate_limited_contact");
		List<String> bodies = new java.util.ArrayList<>();
		for (int i = 0; i < 4; i++) {
			Map<String, Object> form = form();
			form.put("phone", spellings[i]);
			HttpResponse<String> response = submit(form);
			// The same answer past the limit: a 429 would say this phone was already used three times.
			assertThat(response.statusCode()).as("request %d: %s", i + 1, response.body()).isEqualTo(202);
			bodies.add(response.body());
		}
		assertThat(bodies).containsOnly("{\"status\":\"RECEIVED\"}");
		assertThat(counted("rate_limited_contact")).isEqualTo(limited + 1);
		assertThat(Owner.<Long>queryOne("select count(*) from leads where phone = ?", "010" + digits)).isEqualTo(1L);
		// Requests 2 and 3 were counted, each with other details; the fourth was set aside.
		assertThat(Owner.<String>queryOne("select request_count || '/' || differing_requests from leads where phone = ?",
				"010" + digits)).isEqualTo("3/2");
	}

	@Test
	void pastThreeADayAnEmailIsSetAsideSilentlyWhateverItsCapitals() throws Exception {
		String email = "fleet-" + token() + "@example.test";
		double limited = counted("rate_limited_contact");
		for (int i = 0; i < 4; i++) {
			Map<String, Object> form = form();
			form.put("email", i % 2 == 0 ? email : email.toUpperCase());
			assertThat(submit(form).statusCode()).as("request %d", i + 1).isEqualTo(202);
		}
		assertThat(counted("rate_limited_contact")).isEqualTo(limited + 1);
		assertThat(Owner.<Integer>queryOne("select request_count from leads where email = ?", email)).isEqualTo(3);
	}

	@Test
	void aRepeatWithTheSameDetailsIsNotCountedAsDiffering() throws Exception {
		Map<String, Object> form = form();
		submit(form);
		submit(form);
		assertThat(Owner.<String>queryOne("select request_count || '/' || differing_requests from leads where email = ?",
				form.get("email"))).isEqualTo("2/0");
	}

	@Test
	void pastTheHourlyCapOfNewRequestsEverythingIsSetAsideSilently() throws Exception {
		// The whole form's budget: an IPv6 /48 or a botnet spread over many addresses still meets it.
		Owner.update("""
				insert into rate_limit_counters (bucket, key_hash, window_start, expires_at, hits)
				values ('demo-all-hour', ?, to_timestamp(floor(extract(epoch from now()) / 3600) * 3600),
				        to_timestamp(floor(extract(epoch from now()) / 3600) * 3600 + 3600), ?)""",
				hasher.hash("rate-limit:demo-all-hour", "all"), Leads.DEFAULT_MAX_PER_HOUR);
		double capped = counted("global_cap");
		Map<String, Object> form = form();
		HttpResponse<String> response = submit(form);
		assertThat(response.statusCode()).isEqualTo(202);
		assertThat(response.body()).isEqualTo("{\"status\":\"RECEIVED\"}");
		assertThat(Owner.<Long>queryOne("select count(*) from leads where email = ?", form.get("email"))).isZero();
		assertThat(counted("global_cap")).isEqualTo(capped + 1);
	}

	// ------------------------------------------------------------------------- personal data

	@Test
	void theLogCarriesTheReferenceButNoneOfThePersonsData(CapturedOutput output) throws Exception {
		Map<String, Object> form = form();
		submit(form);
		Map<String, Object> repeat = form();
		repeat.put("phone", form.get("phone"));
		submit(repeat);
		noSharedCounters();
		Map<String, Object> invalid = form();
		invalid.put("fleetSize", "lots");
		submit(invalid);
		Map<String, Object> spam = form();
		spam.put("website", "spam");
		submit(spam);

		String reference = Owner.queryOne("select reference from leads where email = ?", form.get("email"));
		assertThat(output.getAll()).contains(reference);
		for (Map<String, Object> f : List.of(form, repeat, invalid, spam)) {
			for (String field : List.of("companyName", "contactName", "phone", "email")) {
				String value = String.valueOf(f.get(field));
				assertThat(output.getAll()).as("%s in the log", field).doesNotContain(value);
			}
		}
	}

	// ------------------------------------------------------------------------------------ helpers

	/** A valid form with its own phone, e-mail and company, shown ten seconds ago. */
	private static Map<String, Object> form() {
		String token = token();
		Map<String, Object> form = new LinkedHashMap<>();
		form.put("companyName", "Delta Logistics " + token);
		form.put("contactName", "Karim " + token);
		form.put("phone", "010" + digits());
		form.put("email", "karim-" + token + "@example.test");
		form.put("fleetSize", "20-49");
		form.put("locale", "ar");
		form.put("website", "");
		form.put("startedAt", Instant.now().minusSeconds(10).toEpochMilli());
		return form;
	}

	private HttpResponse<String> submit(Map<String, Object> form) throws Exception {
		return send(PATH, json.writeValueAsString(form));
	}

	private HttpResponse<String> send(String path, String body) throws Exception {
		return post(path, body);
	}

	private double counted(String outcome) {
		Counter counter = metrics.find(Leads.METRIC).tag("outcome", outcome).counter();
		return counter == null ? 0 : counter.count();
	}

	private static String digits() {
		return String.format("%08d", ThreadLocalRandom.current().nextInt(100_000_000));
	}

	private static String token() {
		return UUID.randomUUID().toString().substring(0, 8);
	}

	private static String arabic(String ascii) {
		StringBuilder out = new StringBuilder();
		ascii.chars().forEach((c) -> out.append(Character.isDigit(c) ? (char) ('٠' + (c - '0')) : (char) c));
		return out.toString();
	}

}
