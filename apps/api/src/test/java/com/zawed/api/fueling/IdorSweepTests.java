package com.zawed.api.fueling;

import static org.assertj.core.api.Assertions.assertThat;

import java.io.ByteArrayOutputStream;
import java.lang.reflect.ParameterizedType;
import java.lang.reflect.RecordComponent;
import java.net.URLEncoder;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.time.Instant;
import java.time.LocalDate;
import java.time.ZoneId;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.EnumMap;
import java.util.EnumSet;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeMap;
import java.util.TreeSet;
import java.util.UUID;
import java.util.concurrent.ThreadLocalRandom;
import java.util.function.UnaryOperator;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

import com.jayway.jsonpath.JsonPath;

import org.assertj.core.api.SoftAssertions;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.beans.factory.annotation.Qualifier;
import org.springframework.core.DefaultParameterNameDiscoverer;
import org.springframework.core.MethodParameter;
import org.springframework.test.context.TestPropertySource;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.method.HandlerMethod;
import org.springframework.web.servlet.mvc.method.annotation.RequestMappingHandlerMapping;

import com.zawed.api.identity.AuthorizationMatrixTests;
import com.zawed.api.platform.ratelimit.InMemoryRateLimiter;
import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.support.Owner;

/**
 * B7 W2: the automated IDOR sweep. Two complete tenants are built through the API (company A and B
 * with departments, vehicles, tags, limits, wallets, top-ups, an adjustment, tickets, fuelings,
 * alerts, a reversal and an import; operator A and B with stations, devices, agents, shifts,
 * payables, a verified bank account, a payout and a sync batch). Then every endpoint is called by
 * every company- and operator-scoped role of tenant A that the authorization matrix lets call it,
 * with tenant B's ids in the path, the query and the body, and each answer must:
 *
 * <ul>
 * <li>be the contract's refusal (404 for a row, 400 for a ticket link), never a 2xx for B's row;
 * <li>be <b>byte for byte the answer an id that does not exist gets</b> (only the request id
 * differs), so a refusal never confirms that B's row exists: no existence oracle;
 * <li>carry nothing of B: none of B's ids, plates, names, worker ids, device codes or references;
 * <li>for a list, search, export or calendar given B's ids as filters, be exactly the answer a
 * nonexistent filter gets, while B's own user gets B's rows for the same filter (the positive
 * control: the filter really selects B's data);
 * <li>for a write, leave every row of tenant B unchanged (a fingerprint of B's tables before and
 * after).
 * </ul>
 *
 * Platform roles see every tenant by design, so the platform-only endpoints are checked the other
 * way round: a scoped role is refused with the same 403 for B's real ids as for ids that do not
 * exist. POS device credentials of operator A are swept against operator B's agents, sessions,
 * fuelings and batches, and the public routes that name an object are checked to need their proof.
 *
 * <p>Endpoints come from the running application, as in {@link AuthorizationMatrixTests}. A new
 * endpoint fails {@link #everyEndpointHasASweepRuleOrAReviewedExemption} until it has a rule or a
 * reviewed exemption, and a new UUID input (path variable, query filter or body field) of a swept
 * endpoint fails it until a probe uses it: nothing ships without a decision.
 *
 * <p>Lives beside the POS test support because the world starts with real pumps and fuelings. The
 * properties are {@code StationFinanceTests}' (bank-change hold 0, so a payout can follow), which
 * also lets both classes share one application context.
 */
@TestPropertySource(properties = { "zawed.finance.bank-change-hold-hours=0",
		"zawed.finance.payout-approval-threshold-piastres=100000", "zawed.sync.offline-exposure-cap-piastres=10000",
		"zawed.sync.company-offline-alert-piastres=40000" })
class IdorSweepTests extends OfflineTestSupport {

	private static final ZoneId CAIRO = ZoneId.of("Africa/Cairo");

	/** The roles that live inside one tenant. Platform roles see every tenant by design. */
	private static final Set<Role> SCOPED_ROLES = Collections
		.unmodifiableSet(EnumSet.of(Role.FLEET_MANAGER, Role.ACCOUNTANT, Role.STATION_MANAGER));

	private static final Pattern PLACEHOLDER = Pattern.compile("\\$\\{([a-zA-Z.]+)}");

	/** A placeholder for one of tenant B's values; {@code ${a.*}} are tenant A's own and the dates are neither. */
	private static final Pattern B_VALUE = Pattern.compile("\\$\\{(?!a\\.|from}|to}|today}|now})[a-zA-Z]+}");

	// ------------------------------------------------------------------------------------- rules

	private enum Kind {

		/** One object by id: the contract's "not found", exactly as for an id that does not exist. */
		ONE,
		/** A list, search, export or calendar with B's ids as filters: exactly a nonexistent filter's answer. */
		FILTER,
		/** A read that takes no id: the caller's own view, with nothing of B in it. */
		OWN,
		/** A change that names B's object: refused exactly as for an id that does not exist; B unchanged. */
		WRITE,
		/** A refusal the contract makes different from "not found" on purpose; reviewed, see the reason. */
		CONTRACT

	}

	/**
	 * One request of the sweep. Placeholders {@code ${name}} take tenant B's value in the attack, a
	 * fresh nonexistent value in its twin, and the same value in both for the {@code a.*} and date
	 * constants.
	 *
	 * @param agentSession the X-Agent-Session header of a POS probe, or null
	 * @param control whether B's own user makes the same request as the positive control
	 */
	private record Probe(Kind kind, String method, String path, String body, String agentSession, int status, String code,
			boolean control, String reason) {

		String pathOnly() {
			int query = path.indexOf('?');
			return query < 0 ? path : path.substring(0, query);
		}

	}

	/** Endpoints that company and operator roles may call. */
	private static final Map<String, List<Probe>> SCOPED = new LinkedHashMap<>();

	/** POS routes, called with operator A's device token. */
	private static final Map<String, List<Probe>> DEVICE = new LinkedHashMap<>();

	/** Public routes whose input names an object; each must need its own proof. */
	private static final Map<String, List<Probe>> PUBLIC = new LinkedHashMap<>();

	/**
	 * Reviewed exemptions: endpoints that take no tenant-owned id at all. The coverage test refuses
	 * an exemption for an endpoint with a UUID path variable, query parameter or body field.
	 */
	private static final Map<String, String> EXEMPT = new LinkedHashMap<>();

	/** Platform-only endpoints: the tenant object behind the path segment before each {@code {var}}. */
	private static final Map<String, String> PLATFORM_IDS = Map.ofEntries(Map.entry("users", "user"),
			Map.entry("companies", "company"), Map.entry("vehicles", "vehicle"), Map.entry("tags", "tag"),
			Map.entry("limits", "limit"), Map.entry("operators", "operator"), Map.entry("stations", "station"),
			Map.entry("devices", "device"), Map.entry("agents", "agent"), Map.entry("topups", "topup"),
			Map.entry("wallet-adjustments", "adjustment"), Map.entry("runs", "reconciliationRun"),
			Map.entry("fuelings", "fueling"), Map.entry("alerts", "alert"), Map.entry("bank-account-changes", "bankChange"),
			Map.entry("payouts", "payout"), Map.entry("reversals", "reversal"), Map.entry("fleet-imports", "importJob"),
			Map.entry("tickets", "companyTicket"));

	/** Platform-only endpoints: the tenant object behind each UUID query filter. */
	private static final Map<String, String> PLATFORM_FILTERS = Map.ofEntries(Map.entry("companyId", "company"),
			Map.entry("operatorId", "operator"), Map.entry("stationId", "station"), Map.entry("vehicleId", "vehicle"),
			Map.entry("deviceId", "device"), Map.entry("agentId", "agent"), Map.entry("departmentId", "department"),
			Map.entry("fuelingId", "fueling"),
			// The audit log: who acted, and the row it is about (any tenant object; a company here).
			Map.entry("actorId", "user"), Map.entry("entityId", "company"));

	static {
		String range = "from=${from}&to=${to}";
		String reply = "{\"body\":\"sweep\"}";

		// ------------------------------------------------------------------- every signed-in user
		rule(SCOPED, "GET /api/v1/auth/me", own("/api/v1/auth/me"));
		rule(SCOPED, "GET /api/v1/auth/sessions", own("/api/v1/auth/sessions"));
		rule(SCOPED, "DELETE /api/v1/auth/sessions/{id}",
				write("DELETE", "/api/v1/auth/sessions/${session}", null, 404, "NOT_FOUND"));

		// --------------------------------------------------------------------------- company portal
		rule(SCOPED, "GET /api/v1/company/profile", own("/api/v1/company/profile"));
		rule(SCOPED, "GET /api/v1/company/departments", own("/api/v1/company/departments"));
		rule(SCOPED, "GET /api/v1/company/vehicles", filter("/api/v1/company/vehicles?departmentId=${department}"),
				filter("/api/v1/company/vehicles?q=${plate}"));
		rule(SCOPED, "GET /api/v1/company/vehicles/{id}", one("/api/v1/company/vehicles/${vehicle}"));
		rule(SCOPED, "GET /api/v1/company/wallet", own("/api/v1/company/wallet"));
		rule(SCOPED, "GET /api/v1/company/wallet/statement", own("/api/v1/company/wallet/statement"));
		rule(SCOPED, "GET /api/v1/company/topups", own("/api/v1/company/topups"));
		rule(SCOPED, "POST /api/v1/company/topups", contract("POST", "/api/v1/company/topups",
				"{\"amountPiastres\":1000,\"bankReference\":\"${topupReference}\",\"transferDate\":\"${today}\"}", null, 409,
				"BANK_REFERENCE_IN_USE",
				"One bank transfer credits once (B3, ADR 0021): a reference already live on ZAWED's receiving account is "
						+ "refused whoever claimed it. It says the reference is taken, never whose, how much or when. "
						+ "Owner question in the W2 report."));
		rule(SCOPED, "POST /api/v1/company/topups/{id}/cancel",
				write("POST", "/api/v1/company/topups/${pendingTopup}/cancel", null, 404, "NOT_FOUND"));
		rule(SCOPED, "GET /api/v1/company/fuelings", filter("/api/v1/company/fuelings?vehicleId=${vehicle}"));
		rule(SCOPED, "GET /api/v1/company/fuelings/{id}", one("/api/v1/company/fuelings/${fueling}"),
				one("/api/v1/company/fuelings/${heldFueling}"));
		rule(SCOPED, "GET /api/v1/company/alerts", own("/api/v1/company/alerts"));
		rule(SCOPED, "GET /api/v1/company/fuelings/export",
				filter("/api/v1/company/fuelings/export?" + range + "&vehicleId=${vehicle}"));
		rule(SCOPED, "GET /api/v1/company/activity", filter("/api/v1/company/activity?" + range + "&vehicleId=${vehicle}"));
		rule(SCOPED, "GET /api/v1/company/reversals", own("/api/v1/company/reversals"));
		rule(SCOPED, "GET /api/v1/company/tickets", own("/api/v1/company/tickets"));
		rule(SCOPED, "POST /api/v1/company/tickets",
				write("POST", "/api/v1/company/tickets", ticket("vehicleId", "${vehicle}"), 400, "TICKET_LINK_NOT_FOUND"),
				write("POST", "/api/v1/company/tickets", ticket("fuelingId", "${fueling}"), 400, "TICKET_LINK_NOT_FOUND"),
				write("POST", "/api/v1/company/tickets", ticket("stationId", "${station}"), 400, "TICKET_LINK_NOT_FOUND"));
		rule(SCOPED, "GET /api/v1/company/tickets/{id}", one("/api/v1/company/tickets/${companyTicket}"),
				otherSide("/api/v1/company/tickets/${operatorTicket}"));
		rule(SCOPED, "POST /api/v1/company/tickets/{id}/messages",
				write("POST", "/api/v1/company/tickets/${companyTicket}/messages", reply, 404, "NOT_FOUND"),
				write("POST", "/api/v1/company/tickets/${operatorTicket}/messages", reply, 404, "NOT_FOUND"));

		// --------------------------------------------------------------------------- station portal
		rule(SCOPED, "GET /api/v1/station/operator", own("/api/v1/station/operator"));
		rule(SCOPED, "GET /api/v1/station/stations", filter("/api/v1/station/stations?q=${stationName}"));
		rule(SCOPED, "GET /api/v1/station/stations/{id}", one("/api/v1/station/stations/${station}"));
		rule(SCOPED, "GET /api/v1/station/devices", filter("/api/v1/station/devices?stationId=${station}"));
		rule(SCOPED, "GET /api/v1/station/agents", filter("/api/v1/station/agents?stationId=${station}"),
				filter("/api/v1/station/agents?q=${workerId}"));
		rule(SCOPED, "GET /api/v1/station/sync/devices", filter("/api/v1/station/sync/devices?stationId=${station}"));
		rule(SCOPED, "GET /api/v1/station/fuelings", filter("/api/v1/station/fuelings?stationId=${station}"));
		rule(SCOPED, "GET /api/v1/station/fuelings/{id}", one("/api/v1/station/fuelings/${fueling}"),
				one("/api/v1/station/fuelings/${heldFueling}"));
		rule(SCOPED, "GET /api/v1/station/fuelings/export",
				filter("/api/v1/station/fuelings/export?" + range + "&stationId=${station}"));
		rule(SCOPED, "GET /api/v1/station/activity", filter("/api/v1/station/activity?" + range + "&stationId=${station}"),
				filter("/api/v1/station/activity?" + range + "&deviceId=${device}"),
				filter("/api/v1/station/activity?" + range + "&agentId=${agent}"));
		rule(SCOPED, "GET /api/v1/station/shifts", filter("/api/v1/station/shifts?stationId=${station}"),
				filter("/api/v1/station/shifts?deviceId=${device}"), filter("/api/v1/station/shifts?agentId=${agent}"));
		rule(SCOPED, "GET /api/v1/station/stations/{id}/payable", one("/api/v1/station/stations/${station}/payable"));
		rule(SCOPED, "GET /api/v1/station/stations/{id}/statement", one("/api/v1/station/stations/${station}/statement"));
		rule(SCOPED, "GET /api/v1/station/stations/{id}/statement/summary",
				one("/api/v1/station/stations/${station}/statement/summary"));
		rule(SCOPED, "GET /api/v1/station/stations/{id}/bank-account",
				one("/api/v1/station/stations/${station}/bank-account"));
		rule(SCOPED, "GET /api/v1/station/payouts", filter("/api/v1/station/payouts?stationId=${station}"));
		rule(SCOPED, "GET /api/v1/station/reversals", filter("/api/v1/station/reversals?stationId=${station}"));
		rule(SCOPED, "GET /api/v1/station/tickets", own("/api/v1/station/tickets"));
		rule(SCOPED, "POST /api/v1/station/tickets",
				write("POST", "/api/v1/station/tickets", ticket("stationId", "${station}"), 400, "TICKET_LINK_NOT_FOUND"),
				write("POST", "/api/v1/station/tickets", ticket("fuelingId", "${fueling}"), 400, "TICKET_LINK_NOT_FOUND"),
				write("POST", "/api/v1/station/tickets", ticket("vehicleId", "${vehicle}"), 400, "TICKET_LINK_NOT_FOUND"));
		rule(SCOPED, "GET /api/v1/station/tickets/{id}", one("/api/v1/station/tickets/${operatorTicket}"),
				otherSide("/api/v1/station/tickets/${companyTicket}"));
		rule(SCOPED, "POST /api/v1/station/tickets/{id}/messages",
				write("POST", "/api/v1/station/tickets/${operatorTicket}/messages", reply, 404, "NOT_FOUND"),
				write("POST", "/api/v1/station/tickets/${companyTicket}/messages", reply, 404, "NOT_FOUND"));

		// ----------------------------------------------------------- POS: operator A's device token
		String capture = "{\"batchId\":\"%s\",\"pending\":0,\"items\":[{\"clientTxnId\":\"%s\",\"tagUid\":\"%s\","
				+ "\"volumeMl\":20000,\"deviceAt\":\"${now}\",\"agentId\":\"%s\",\"receiptLocale\":\"ar\"}]}";
		rule(DEVICE, "POST /api/v1/pos/agents/login", pos(Kind.WRITE, "POST", "/api/v1/pos/agents/login",
				"{\"workerId\":\"${workerId}\",\"pin\":\"${pin}\"}", null, 401, "AGENT_SIGN_IN_FAILED"));
		rule(DEVICE, "POST /api/v1/pos/agents/pin", pos(Kind.WRITE, "POST", "/api/v1/pos/agents/pin",
				"{\"currentPin\":\"${pin}\",\"newPin\":\"739154\"}", "${agentSession}", 401, "AGENT_SESSION_INVALID"));
		rule(DEVICE, "POST /api/v1/pos/agents/logout",
				pos(Kind.WRITE, "POST", "/api/v1/pos/agents/logout", null, "${agentSession}", 401, "AGENT_SESSION_INVALID"));
		rule(DEVICE, "POST /api/v1/pos/fuelings",
				pos(Kind.WRITE, "POST", "/api/v1/pos/fuelings", fuelingBody("${a.txn}", "${a.tag}"), "${agentSession}", 401,
						"AGENT_SESSION_INVALID"),
				new Probe(Kind.CONTRACT, "POST", "/api/v1/pos/fuelings", fuelingBody("${clientTxnId}", "${fuelingTag}"),
						"${a.session}", 409, "CLIENT_TXN_CONFLICT", false,
						"B4 contract §3: a clientTxnId is unique platform-wide (invariant 3), so another device's id answers "
								+ "409 CLIENT_TXN_CONFLICT, not the stored outcome. The id is a device-generated UUID, and the "
								+ "answer carries nothing of the stored fueling."));
		rule(DEVICE, "POST /api/v1/pos/fuelings/{clientTxnId}/resolve",
				pos(Kind.WRITE, "POST", "/api/v1/pos/fuelings/${clientTxnId}/resolve", "{\"toldOutcome\":\"REFUSED\"}",
						"${a.session}", 404, "FUELING_NOT_FOUND"));
		rule(DEVICE, "POST /api/v1/pos/sync",
				new Probe(Kind.CONTRACT, "POST", "/api/v1/pos/sync",
						capture.formatted("${a.batch}", "${clientTxnId}", "${fuelingTag}", "${a.agent}"), null, 200,
						"CLIENT_TXN_CONFLICT", false, "B5 contract §3: the same platform-wide clientTxnId rule, per item."),
				pos(Kind.WRITE, "POST", "/api/v1/pos/sync",
						capture.formatted("${a.batch}", "${a.txn}", "${a.tag}", "${agent}"), null, 200, "AGENT_NOT_OF_STATION"),
				pos(Kind.WRITE, "POST", "/api/v1/pos/sync", "{\"batchId\":\"${batch}\",\"pending\":0,\"items\":[]}", null, 200,
						null));
		rule(DEVICE, "GET /api/v1/pos/snapshot", pos(Kind.OWN, "GET", "/api/v1/pos/snapshot", null, null, 200, null));

		// ---------------------------------------------------- public routes that name an object
		rule(PUBLIC, "GET /api/v1/receipts/{id}",
				pos(Kind.ONE, "GET", "/api/v1/receipts/${fueling}?sig=${a.receiptSig}", null, null, 404, "NOT_FOUND"),
				pos(Kind.ONE, "GET", "/api/v1/receipts/${fueling}", null, null, 404, "NOT_FOUND"));
		rule(PUBLIC, "POST /api/v1/pos/token", pos(Kind.WRITE, "POST", "/api/v1/pos/token",
				"{\"deviceId\":\"${device}\",\"timestamp\":${a.epoch},\"nonce\":\"${a.nonce}\",\"signature\":\"${a.signature}\"}",
				null, 401, "DEVICE_UNAUTHENTICATED"));
		rule(PUBLIC, "POST /api/v1/pos/activate", pos(Kind.WRITE, "POST", "/api/v1/pos/activate",
				"{\"deviceCode\":\"${deviceCode}\",\"activationCode\":\"WRONGCODE123\",\"publicKey\":\"${a.publicKey}\"}", null,
				401, "ACTIVATION_FAILED"));

		// ------------------------------------------------------------------ reviewed exemptions
		String signIn = "sign-in step: an email, a password or a challenge token issued to this sign-in; no object id";
		String cookie = "the refresh cookie is the credential and names only the caller's own session";
		EXEMPT.put("GET /api/v1/meta", "service metadata (version, schema); names no tenant object");
		EXEMPT.put("POST /api/v1/auth/login", signIn);
		EXEMPT.put("POST /api/v1/auth/mfa/verify", signIn);
		EXEMPT.put("POST /api/v1/auth/mfa/enroll/start", signIn);
		EXEMPT.put("POST /api/v1/auth/mfa/enroll/confirm", signIn);
		EXEMPT.put("POST /api/v1/admin/auth/login", signIn);
		EXEMPT.put("POST /api/v1/auth/refresh", cookie);
		EXEMPT.put("POST /api/v1/auth/logout", cookie);
		EXEMPT.put("POST /api/v1/admin/auth/refresh", cookie);
		EXEMPT.put("POST /api/v1/admin/auth/logout", cookie);
		EXEMPT.put("POST /api/v1/setup/super-admin", "one-time bootstrap with the setup token; no tenant exists yet");
		EXEMPT.put("POST /api/v1/auth/password", "changes the caller's own password; takes no id");
	}

	private static void rule(Map<String, List<Probe>> rules, String endpoint, Probe... probes) {
		rules.put(endpoint, List.of(probes));
	}

	private static Probe one(String path) {
		return new Probe(Kind.ONE, "GET", path, null, null, 404, "NOT_FOUND", true, null);
	}

	/** One object of B's other side (a company's ticket asked for in the station portal): B's user cannot read it either. */
	private static Probe otherSide(String path) {
		return new Probe(Kind.ONE, "GET", path, null, null, 404, "NOT_FOUND", false, null);
	}

	private static Probe filter(String path) {
		return new Probe(Kind.FILTER, "GET", path, null, null, 200, null, true, null);
	}

	private static Probe own(String path) {
		return new Probe(Kind.OWN, "GET", path, null, null, 200, null, true, null);
	}

	private static Probe write(String method, String path, String body, int status, String code) {
		return new Probe(Kind.WRITE, method, path, body, null, status, code, false, null);
	}

	private static Probe contract(String method, String path, String body, String agentSession, int status, String code,
			String reason) {
		return new Probe(Kind.CONTRACT, method, path, body, agentSession, status, code, false, reason);
	}

	private static Probe pos(Kind kind, String method, String path, String body, String agentSession, int status,
			String code) {
		return new Probe(kind, method, path, body, agentSession, status, code, false, null);
	}

	/** A ticket both portals may open (DISPUTE), linking one record. */
	private static String ticket(String field, String value) {
		return "{\"type\":\"DISPUTE\",\"subject\":\"sweep\",\"body\":\"sweep\",\"" + field + "\":\"" + value + "\"}";
	}

	private static String fuelingBody(String clientTxnId, String tagUid) {
		return "{\"clientTxnId\":\"" + clientTxnId + "\",\"tagUid\":\"" + tagUid
				+ "\",\"volumeMl\":20000,\"deviceAt\":\"${now}\",\"receiptLocale\":\"ar\"}";
	}

	// --------------------------------------------------------------------------- infrastructure

	@Autowired
	@Qualifier("requestMappingHandlerMapping")
	RequestMappingHandlerMapping mappings;

	/** The limiter itself: the sweep makes far more requests per user than a minute's budget. */
	@Autowired
	InMemoryRateLimiter rates;

	private static World world;

	@BeforeEach
	void buildTheTwoTenantsOnce() throws Exception {
		if (world == null) {
			world = build();
		}
		rates.reset();
	}

	// ------------------------------------------------------------------------------------- tests

	@Test
	void everyEndpointHasASweepRuleOrAReviewedExemption() {
		Map<String, HandlerMethod> registered = AuthorizationMatrixTests.registeredEndpoints(mappings);
		SoftAssertions softly = new SoftAssertions();
		Map<Category, Integer> counts = new EnumMap<>(Category.class);
		for (var entry : registered.entrySet()) {
			String endpoint = entry.getKey();
			Category category = category(endpoint);
			counts.merge(category, 1, Integer::sum);
			Set<String> inputs = uuidInputs(entry.getValue());
			switch (category) {
				case UNDECIDED -> softly.fail("%s has no sweep rule and no reviewed exemption (inputs %s)", endpoint, inputs);
				case EXEMPT -> softly.assertThat(inputs)
					.as("%s is exempt but takes ids %s: it needs a sweep rule instead", endpoint, inputs)
					.isEmpty();
				case PLATFORM -> {
					for (String input : inputs) {
						if (input.startsWith("query:")) {
							softly.assertThat(PLATFORM_FILTERS).as("%s: no tenant object for filter %s", endpoint, input)
								.containsKey(input.substring(6));
						}
					}
					for (String var : pathVariables(endpoint)) {
						softly.assertThat(PLATFORM_IDS).as("%s: no tenant object for the id after /%s/", endpoint, var)
							.containsKey(var);
					}
				}
				default -> covers(softly, endpoint, inputs, rulesOf(category).get(endpoint));
			}
		}
		for (Map<String, ?> rules : List.of(SCOPED, DEVICE, PUBLIC, EXEMPT)) {
			for (String endpoint : rules.keySet()) {
				softly.assertThat(registered).as("a sweep rule for an endpoint that no longer exists").containsKey(endpoint);
			}
		}
		for (String endpoint : EXEMPT.keySet()) {
			softly.assertThat(EXEMPT.get(endpoint)).as("the reason for exempting %s", endpoint).isNotBlank();
			softly.assertThat(SCOPED.containsKey(endpoint) || DEVICE.containsKey(endpoint) || PUBLIC.containsKey(endpoint))
				.as("%s is both exempt and swept", endpoint)
				.isFalse();
		}
		softly.assertAll();
		System.out.printf("IDOR sweep: %d endpoints: %s%n", registered.size(), counts);
	}

	@Test
	void scopedRolesOfOneTenantNeverReachAnotherTenantsObjects() throws Exception {
		World w = world;
		Map<String, String> tenantB = fingerprint(w.companyB, w.operatorB);
		String tenantA = ownWrites(w);
		SoftAssertions softly = new SoftAssertions();
		int cases = 0;
		for (var rule : SCOPED.entrySet()) {
			String endpoint = rule.getKey();
			Set<Role> roles = scopedRolesOf(endpoint);
			softly.assertThat(roles).as("%s: the matrix lets no scoped role call it", endpoint).isNotEmpty();
			for (Probe probe : rule.getValue()) {
				for (Role role : roles) {
					cases += attack(softly, endpoint + " as " + role + " of A", probe, Caller.user(w.usersA.get(role)), w.markersB);
				}
				if (probe.control() && !roles.isEmpty()) {
					control(softly, endpoint, probe, Caller.user(w.usersB.get(roles.iterator().next())));
				}
			}
		}
		softly.assertThat(fingerprint(w.companyB, w.operatorB)).as("tenant B's rows after the sweep").isEqualTo(tenantB);
		softly.assertThat(ownWrites(w)).as("what tenant A wrote: every write that named B must have failed")
			.isEqualTo(tenantA);
		softly.assertAll();
		System.out.printf("IDOR sweep, scoped: %d endpoints, %d role x endpoint x id cases, %d markers of tenant B%n",
				SCOPED.size(), cases, w.markersB.size());
	}

	@Test
	void platformOnlyEndpointsRefuseScopedRolesTheSameForRealAndMissingIds() throws Exception {
		World w = world;
		Map<String, String> tenantB = fingerprint(w.companyB, w.operatorB);
		SoftAssertions softly = new SoftAssertions();
		int endpoints = 0;
		int cases = 0;
		for (var entry : AuthorizationMatrixTests.registeredEndpoints(mappings).entrySet()) {
			String endpoint = entry.getKey();
			if (category(endpoint) != Category.PLATFORM) {
				continue;
			}
			endpoints++;
			String method = endpoint.substring(0, endpoint.indexOf(' '));
			Probe probe = new Probe(Kind.WRITE, method, platformPath(endpoint, entry.getValue()),
					AuthorizationMatrixTests.validBody(endpoint), null, 403, "FORBIDDEN", false, null);
			for (Role role : SCOPED_ROLES) {
				cases += attack(softly, endpoint + " as " + role + " of A", probe, Caller.user(w.usersA.get(role)), w.markersB);
			}
		}
		softly.assertThat(fingerprint(w.companyB, w.operatorB)).as("tenant B's rows after the sweep").isEqualTo(tenantB);
		softly.assertAll();
		System.out.printf("IDOR sweep, platform-only: %d endpoints, %d role x endpoint x id cases%n", endpoints, cases);
	}

	@Test
	void aPosDeviceOfOneOperatorNeverReachesAnotherOperatorsAgentsFuelingsOrBatches() throws Exception {
		World w = world;
		Map<String, String> tenantB = fingerprint(w.companyB, w.operatorB);
		String recordedByA = deviceWrites(w);
		SoftAssertions softly = new SoftAssertions();
		int cases = 0;
		for (var rule : DEVICE.entrySet()) {
			for (Probe probe : rule.getValue()) {
				// The snapshot carries every company's fleet by design (B5 contract §2): only operator B is foreign to it.
				Set<String> markers = probe.kind() == Kind.OWN ? w.operatorMarkersB : w.markersB;
				cases += attack(softly, rule.getKey() + " from operator A's device", probe, Caller.device(w.pumpA), markers);
			}
		}
		softly.assertThat(fingerprint(w.companyB, w.operatorB)).as("tenant B's rows after the sweep").isEqualTo(tenantB);
		softly.assertThat(deviceWrites(w)).as("fuelings recorded by operator A's device: none of the probes may record one")
			.isEqualTo(recordedByA);
		softly.assertAll();
		System.out.printf("IDOR sweep, POS: %d endpoints, %d device x endpoint x id cases%n", DEVICE.size(), cases);
	}

	@Test
	void publicRoutesThatNameAnObjectNeedTheirOwnProof() throws Exception {
		World w = world;
		Map<String, String> tenantB = fingerprint(w.companyB, w.operatorB);
		SoftAssertions softly = new SoftAssertions();
		int cases = 0;
		for (var rule : PUBLIC.entrySet()) {
			for (Probe probe : rule.getValue()) {
				cases += attack(softly, rule.getKey() + " anonymously", probe, Caller.anonymous(), w.markersB);
			}
		}
		// The positive control: B's own receipt link, with its own signature, does open.
		HttpResponse<String> receipt = get(w.receiptPathB);
		softly.assertThat(receipt.statusCode()).as("B's own receipt link").isEqualTo(200);
		softly.assertThat(fingerprint(w.companyB, w.operatorB)).as("tenant B's rows after the sweep").isEqualTo(tenantB);
		softly.assertAll();
		System.out.printf("IDOR sweep, public: %d endpoints, %d endpoint x id cases%n", PUBLIC.size(), cases);
	}

	// ------------------------------------------------------------------------------ the checks

	/**
	 * B's ids, then a twin with fresh ids that do not exist: the contract's answer, nothing of B in
	 * it, and the same answer for both. Returns the number of attack requests made.
	 */
	private int attack(SoftAssertions softly, String who, Probe probe, Caller caller, Set<String> markers) throws Exception {
		rates.reset();
		Sent hit = send(probe, caller, world.b::get);
		softly.assertThat(hit.status()).as("%s %s: status (%s)", who, hit.path(), hit.body()).isEqualTo(probe.status());
		if (probe.code() != null) {
			softly.assertThat(hit.code()).as("%s %s: code (%s)", who, hit.path(), hit.body()).isEqualTo(probe.code());
		}
		softly.assertThat(hit.leaks(markers)).as("%s %s: tenant B's data in the answer %s", who, hit.path(), hit.body())
			.isEmpty();
		if (probe.kind() != Kind.OWN && probe.kind() != Kind.CONTRACT) {
			Map<String, String> twin = new HashMap<>();
			Sent ghost = send(probe, caller, (name) -> twin.computeIfAbsent(name, (n) -> twinOf(n, world.b.get(n))));
			softly.assertThat(hit.normalized())
				.as("%s %s: B's ids must be answered exactly as ids that do not exist", who, hit.path())
				.isEqualTo(ghost.normalized());
		}
		return 1;
	}

	/** The positive control: B's own user does get B's object, or B's rows for the same filter. */
	private void control(SoftAssertions softly, String endpoint, Probe probe, Caller owner) throws Exception {
		rates.reset();
		Sent mine = send(probe, owner, world.b::get);
		softly.assertThat(mine.status()).as("control: B's own %s %s (%s)", endpoint, mine.path(), mine.body()).isEqualTo(200);
		switch (probe.kind()) {
			case FILTER -> {
				Map<String, String> twin = new HashMap<>();
				Sent none = send(probe, owner, (name) -> twin.computeIfAbsent(name, (n) -> twinOf(n, world.b.get(n))));
				softly.assertThat(mine.normalized())
					.as("control: B's own %s must select B's rows, or the probe proves nothing", mine.path())
					.isNotEqualTo(none.normalized());
			}
			case OWN -> softly.assertThat(mine.leaks(world.markersB))
				.as("control: B's own %s must show B's data, or the probe proves nothing", mine.path())
				.isNotEmpty();
			default -> {
				// ONE: the 200 above is the proof that the id is real and readable by its owner.
			}
		}
	}

	/** Who sends a probe: a signed-in user, a POS device (with the probe's agent session), or nobody. */
	private record Caller(SignedIn user, Pump device) {

		static Caller user(SignedIn user) {
			return new Caller(user, null);
		}

		static Caller device(Pump pump) {
			return new Caller(null, pump);
		}

		static Caller anonymous() {
			return new Caller(null, null);
		}

	}

	/** An answer, with the placeholder values the request carried (an echo of them is not a leak). */
	private record Sent(String path, int status, String body, Map<String, String> sent) {

		String code() {
			if (body == null || body.isBlank() || !body.startsWith("{")) {
				return null;
			}
			try {
				if (body.contains("\"results\"")) {
					List<String> codes = JsonPath.read(body, "$.results[*].code");
					return codes.isEmpty() ? null : codes.getFirst();
				}
				return body.contains("\"code\"") ? JsonPath.read(body, "$.code") : null;
			}
			catch (RuntimeException ex) {
				return null;
			}
		}

		/** The answer with the request id blanked and each value the request sent put back as its placeholder. */
		String normalized() {
			String text = body == null ? "" : body.replaceAll("\"requestId\":\"[^\"]*\"", "\"requestId\":\"\"");
			List<Map.Entry<String, String>> values = new ArrayList<>(sent.entrySet());
			values.sort(Comparator.comparingInt((Map.Entry<String, String> e) -> e.getValue().length()).reversed());
			for (var value : values) {
				text = text.replace(value.getValue(), "${" + value.getKey() + "}");
			}
			return status + " " + text;
		}

		/** Markers of B found in the answer, except the values this request itself sent. */
		Set<String> leaks(Set<String> markers) {
			Set<String> found = new TreeSet<>();
			if (body == null) {
				return found;
			}
			for (String marker : markers) {
				if (!sent.containsValue(marker) && body.contains(marker)) {
					found.add(marker);
				}
			}
			return found;
		}

	}

	private Sent send(Probe probe, Caller caller, UnaryOperator<String> lookup) throws Exception {
		Map<String, String> sent = new LinkedHashMap<>();
		String path = resolve(probe.path(), lookup, sent);
		String body = probe.body() == null ? null : resolve(probe.body(), lookup, sent);
		String session = probe.agentSession() == null ? null : resolve(probe.agentSession(), lookup, sent);
		if (body != null && body.contains("${a.signature}")) {
			// The device token request, signed by operator A's own key over the device id it names.
			String device = JsonPath.read(body.replace("${a.signature}", ""), "$.deviceId");
			long epoch = Instant.now().getEpochSecond();
			String nonce = UUID.randomUUID().toString();
			body = body.replace("\"timestamp\":" + world.constants.get("a.epoch"), "\"timestamp\":" + epoch)
				.replace(world.constants.get("a.nonce"), nonce)
				.replace("${a.signature}", sign(world.pumpA.keys(), "zawed-device-token\n" + device + "\n" + epoch + "\n" + nonce));
		}
		HttpResponse<String> response;
		if (caller.device() != null) {
			response = pos(probe.method(), path, caller.device().deviceToken(), session, body);
		}
		else if (caller.user() != null) {
			boolean noBody = "GET".equals(probe.method()) || "DELETE".equals(probe.method());
			response = authed(probe.method(), path, caller.user().accessToken(), noBody ? null : body);
		}
		else {
			response = "GET".equals(probe.method()) ? get(path) : post(path, body == null ? "{}" : body);
		}
		return new Sent(path, response.statusCode(), response.body(), sent);
	}

	/**
	 * Replaces each placeholder. Constants are the same in an attack and its twin; every other value
	 * is recorded as sent. Values after the {@code ?} are URL-encoded.
	 */
	private static String resolve(String template, UnaryOperator<String> lookup, Map<String, String> sent) {
		int query = template.indexOf('?');
		StringBuilder out = new StringBuilder();
		Matcher matcher = PLACEHOLDER.matcher(template);
		while (matcher.find()) {
			String name = matcher.group(1);
			if ("a.signature".equals(name)) {
				matcher.appendReplacement(out, Matcher.quoteReplacement(matcher.group()));
				continue;
			}
			String value = world.constants.containsKey(name) ? world.constants.get(name) : lookup.apply(name);
			if (value == null) {
				throw new IllegalStateException("No value for ${" + name + "} in " + template);
			}
			if (!world.constants.containsKey(name)) {
				sent.put(name, value);
			}
			String text = query >= 0 && matcher.start() > query ? URLEncoder.encode(value, StandardCharsets.UTF_8) : value;
			matcher.appendReplacement(out, Matcher.quoteReplacement(text));
		}
		matcher.appendTail(out);
		return out.toString();
	}

	/** A value of the same kind as B's that names nothing: a fresh UUID, plate, worker ID, token… */
	private static String twinOf(String name, String value) {
		if (value == null) {
			throw new IllegalStateException("No value of tenant B for ${" + name + "}");
		}
		return switch (name) {
			case "plate" -> "ZZ " + hex(8).toUpperCase();
			case "stationName" -> hex(value.length());
			case "workerId" -> "W" + digits(value.length() - 1);
			case "pin" -> digits(value.length());
			case "agentSession" -> hex(value.length());
			case "fuelingTag" -> randomUid();
			case "deviceCode" -> value.substring(0, value.indexOf('-') + 1) + hex(value.length() - value.indexOf('-') - 1)
				.toUpperCase();
			case "topupReference" -> "ZZ" + hex(12).toUpperCase();
			default -> {
				UUID.fromString(value); // anything else must be an id; a new kind of value needs a twin above
				yield UUID.randomUUID().toString();
			}
		};
	}

	// ---------------------------------------------------------------------------- classification

	private enum Category {

		EXEMPT, SCOPED, DEVICE, PUBLIC, PLATFORM, UNDECIDED

	}

	private static Category category(String endpoint) {
		if (EXEMPT.containsKey(endpoint)) {
			return Category.EXEMPT;
		}
		Set<Role> allowed = AuthorizationMatrixTests.allowedRoles(endpoint);
		if (allowed == null) {
			return Category.UNDECIDED;
		}
		if (AuthorizationMatrixTests.isDeviceRoute(endpoint)) {
			return DEVICE.containsKey(endpoint) ? Category.DEVICE : Category.UNDECIDED;
		}
		if (AuthorizationMatrixTests.isPublicRoute(endpoint)) {
			return PUBLIC.containsKey(endpoint) ? Category.PUBLIC : Category.UNDECIDED;
		}
		if (allowed.stream().anyMatch(SCOPED_ROLES::contains)) {
			return SCOPED.containsKey(endpoint) ? Category.SCOPED : Category.UNDECIDED;
		}
		return Category.PLATFORM;
	}

	private static Map<String, List<Probe>> rulesOf(Category category) {
		return switch (category) {
			case SCOPED -> SCOPED;
			case DEVICE -> DEVICE;
			case PUBLIC -> PUBLIC;
			default -> throw new IllegalArgumentException(category.name());
		};
	}

	private static Set<Role> scopedRolesOf(String endpoint) {
		Set<Role> roles = EnumSet.noneOf(Role.class);
		roles.addAll(AuthorizationMatrixTests.allowedRoles(endpoint));
		roles.retainAll(SCOPED_ROLES);
		return roles;
	}

	/**
	 * Each probe is a request to its own endpoint, with a placeholder wherever the route has an id;
	 * and every UUID input the handler takes (a query filter, a body field) is used by some probe.
	 */
	private static void covers(SoftAssertions softly, String endpoint, Set<String> inputs, List<Probe> probes) {
		String method = endpoint.substring(0, endpoint.indexOf(' '));
		String[] route = endpoint.substring(method.length() + 1).split("/");
		for (Probe probe : probes) {
			softly.assertThat(probe.method()).as("%s: probe method", endpoint).isEqualTo(method);
			String[] path = probe.pathOnly().split("/");
			softly.assertThat(path.length).as("%s: probe %s", endpoint, probe.path()).isEqualTo(route.length);
			for (int i = 0; i < Math.min(route.length, path.length); i++) {
				boolean variable = route[i].startsWith("{");
				softly.assertThat(variable ? B_VALUE.matcher(path[i]).lookingAt() : path[i].equals(route[i]))
					.as("%s: segment %d of probe %s must be tenant B's id", endpoint, i, probe.path())
					.isTrue();
			}
			if (probe.kind() == Kind.CONTRACT) {
				softly.assertThat(probe.reason()).as("%s: why the contract answers otherwise", endpoint).isNotBlank();
			}
		}
		for (String input : inputs) {
			String name = input.substring(input.indexOf(':') + 1);
			// Tenant A's own constants (${a.*}) do not count: the input must carry one of B's values.
			Pattern query = Pattern.compile("[?&]" + Pattern.quote(name) + "=" + B_VALUE.pattern());
			Pattern field = Pattern.compile("\"" + Pattern.quote(name) + "\":\"" + B_VALUE.pattern());
			boolean used = switch (input.substring(0, input.indexOf(':'))) {
				case "query" -> probes.stream().anyMatch((p) -> query.matcher(p.path()).find());
				case "body" -> probes.stream().anyMatch((p) -> p.body() != null && field.matcher(p.body()).find());
				default -> true; // path variables: the segment check above
			};
			softly.assertThat(used).as("%s takes %s, but no probe sends one of tenant B's ids there", endpoint, input).isTrue();
		}
	}

	/** The UUID inputs of a handler: {@code path:x}, {@code query:x} and {@code body:x} (nested records and lists too). */
	private static Set<String> uuidInputs(HandlerMethod handler) {
		Set<String> inputs = new TreeSet<>();
		for (MethodParameter parameter : handler.getMethodParameters()) {
			parameter.initParameterNameDiscovery(new DefaultParameterNameDiscoverer());
			PathVariable path = parameter.getParameterAnnotation(PathVariable.class);
			if (path != null) {
				inputs.add("path:" + name(path.value(), parameter));
			}
			RequestParam query = parameter.getParameterAnnotation(RequestParam.class);
			if (query != null && parameter.getParameterType() == UUID.class) {
				inputs.add("query:" + name(query.value(), parameter));
			}
			if (parameter.hasParameterAnnotation(RequestBody.class)) {
				bodyInputs(parameter.getParameterType(), inputs, 0);
			}
		}
		return inputs;
	}

	private static void bodyInputs(Class<?> type, Set<String> inputs, int depth) {
		if (!type.isRecord() || depth > 3) {
			return;
		}
		for (RecordComponent component : type.getRecordComponents()) {
			Class<?> field = component.getType();
			if (field == UUID.class) {
				inputs.add("body:" + component.getName());
			}
			else if (field.isRecord()) {
				bodyInputs(field, inputs, depth + 1);
			}
			else if (java.util.Collection.class.isAssignableFrom(field)
					&& component.getGenericType() instanceof ParameterizedType generic
					&& generic.getActualTypeArguments()[0] instanceof Class<?> element) {
				bodyInputs(element, inputs, depth + 1);
			}
		}
	}

	private static String name(String declared, MethodParameter parameter) {
		return declared == null || declared.isEmpty() ? parameter.getParameterName() : declared;
	}

	/** The path segments that precede each {@code {var}} of a route: what kind of object it names. */
	private static List<String> pathVariables(String endpoint) {
		String[] segments = endpoint.substring(endpoint.indexOf(' ') + 1).split("/");
		List<String> kinds = new ArrayList<>();
		for (int i = 1; i < segments.length; i++) {
			if (segments[i].startsWith("{")) {
				kinds.add(segments[i - 1]);
			}
		}
		return kinds;
	}

	/** A platform-only route with B's ids in every path variable and every UUID filter it takes. */
	private static String platformPath(String endpoint, HandlerMethod handler) {
		String[] segments = endpoint.substring(endpoint.indexOf(' ') + 1).split("/");
		for (int i = 1; i < segments.length; i++) {
			if (segments[i].startsWith("{")) {
				segments[i] = "${" + PLATFORM_IDS.get(segments[i - 1]) + "}";
			}
		}
		StringBuilder path = new StringBuilder(String.join("/", segments));
		char separator = '?';
		for (String input : uuidInputs(handler)) {
			if (input.startsWith("query:")) {
				String filter = input.substring(6);
				path.append(separator).append(filter).append("=${").append(PLATFORM_FILTERS.get(filter)).append('}');
				separator = '&';
			}
		}
		return path.toString();
	}

	// ------------------------------------------------------------------------- the two tenants

	/** Both tenants, built once for the class through the API. */
	private static final class World {

		/** Placeholder → tenant B's value. */
		final Map<String, String> b = new HashMap<>();
		/** The same in an attack and its twin: dates, and tenant A's own credentials and records. */
		final Map<String, String> constants = new HashMap<>();
		final Map<Role, SignedIn> usersA = new EnumMap<>(Role.class);
		final Map<Role, SignedIn> usersB = new EnumMap<>(Role.class);
		/** Everything that would betray tenant B in an answer: ids, plates, names, codes, references. */
		Set<String> markersB;
		/** Operator B's alone. */
		Set<String> operatorMarkersB;
		String companyA;
		String operatorA;
		String companyB;
		String operatorB;
		Pump pumpA;
		String receiptPathB;

	}

	private World build() throws Exception {
		World w = new World();
		SignedIn checker = signIn(seed(Role.OPS)); // the second person of every two-person step
		rates.reset();
		LocalDate today = LocalDate.now(CAIRO);

		// Tenant A: enough that its own lists are not empty, and a pump for the POS probes.
		String tokenA = hex(12);
		w.companyA = company("Alpha " + tokenA);
		String departmentA = department(w.companyA, "Alpha " + tokenA);
		w.operatorA = operatorNamed("Alpha " + tokenA);
		String stationA = stationNamed(w.operatorA, "Alpha " + tokenA);
		price(stationA, "GAS_92", PRICE);
		w.pumpA = pumpAt(w.operatorA, stationA);
		rates.reset();
		credit(w.companyA, 1_000_000);
		Car carA = vehicle(w.companyA, departmentA, 60_000);
		HttpResponse<String> fueledA = fuel(w.pumpA, carA, 20_000);
		expect(fueledA, 201);
		String receiptA = JsonPath.read(fueledA.body(), "$.receiptPath");

		// Tenant B: one of everything a tenant owns. Company and operator names carry their own token.
		String companyToken = hex(12);
		String operatorToken = hex(12);
		w.companyB = company("Bravo " + companyToken);
		String departmentB = department(w.companyB, "Bravo " + companyToken);
		w.operatorB = operatorNamed("Bravo " + operatorToken);
		String stationB = stationNamed(w.operatorB, "Bravo " + operatorToken);
		price(stationB, "GAS_92", PRICE);
		Pump pumpB = pumpAt(w.operatorB, stationB);
		rates.reset();
		credit(w.companyB, 1_000_000);
		Car b1 = vehicle(w.companyB, departmentB, 15_000); // 20 L into a 15 L tank: a company-visible alert
		Car b2 = vehicle(w.companyB, null, 60_000);
		Car b3 = vehicle(w.companyB, null, 60_000);
		HttpResponse<String> limit = authed("PUT", "/api/v1/ops/vehicles/" + b1.vehicleId() + "/limits", ops.accessToken(),
				"{\"kind\":\"VOLUME\",\"period\":\"MONTH\",\"maxValue\":10000000}");
		expect(limit, 200);
		UUID clientTxnId = UUID.randomUUID();
		HttpResponse<String> fueled = fuel(pumpB, b1, clientTxnId, 20_000, null);
		expect(fueled, 201);
		w.receiptPathB = JsonPath.read(fueled.body(), "$.receiptPath");
		HttpResponse<String> reversed = fuel(pumpB, b2, 20_000);
		expect(reversed, 201);
		UUID batch = UUID.randomUUID();
		HttpResponse<String> synced = sync(pumpB, batch, 0, List.of(capture(pumpB, b3, 20_000)));
		assertThat(conflicts(synced, 0)).as("over this class's offline cap: held").contains("OVER_OFFLINE_ALLOWANCE");

		// Station finance: an approved reversal and a posted payout to a verified account, two people each.
		String reversal = id(authed("POST", "/api/v1/ops/fuelings/" + id(reversed) + "/reversals", ops.accessToken(),
				"{\"reason\":\"Charged the wrong vehicle\"}"), 201);
		expect(authed("POST", "/api/v1/ops/reversals/" + reversal + "/approve", checker.accessToken(), null), 200);
		String bankChange = id(authed("POST", "/api/v1/ops/stations/" + stationB + "/bank-account/changes",
				ops.accessToken(), "{\"accountNumber\":\"EG38 0019 0005 0000 0000 2631 80002\",\"bankName\":\"Bank\","
						+ "\"holderName\":\"Bravo " + operatorToken + "\",\"bankLetterReference\":\"BL-" + operatorToken + "\"}"),
				201);
		expect(authed("POST", "/api/v1/ops/bank-account-changes/" + bankChange + "/approve", checker.accessToken(), null),
				200);
		String payout = id(authed("POST", "/api/v1/ops/stations/" + stationB + "/payouts", ops.accessToken(),
				"{\"amountPiastres\":20000,\"bankReference\":\"PO" + operatorToken + "\",\"valueDate\":\"" + today + "\"}"),
				201);

		// The users of both tenants.
		for (Role role : SCOPED_ROLES) {
			UUID tenantA = UUID.fromString(role == Role.STATION_MANAGER ? w.operatorA : w.companyA);
			UUID tenantB = UUID.fromString(role == Role.STATION_MANAGER ? w.operatorB : w.companyB);
			w.usersA.put(role, signIn(seed(role, new Scope(role.scopeType(), tenantA), false)));
			rates.reset();
			w.usersB.put(role, signIn(seed(role, new Scope(role.scopeType(), tenantB), false)));
			rates.reset();
		}
		SignedIn fleetB = w.usersB.get(Role.FLEET_MANAGER);
		SignedIn stationManagerB = w.usersB.get(Role.STATION_MANAGER);

		// Money asked for and not yet decided: the company's pending top-up, an adjustment waiting for a checker.
		String topUpReference = "BRV" + companyToken.toUpperCase();
		String pendingTopUp = id(authed("POST", "/api/v1/company/topups", fleetB.accessToken(),
				"{\"amountPiastres\":5000,\"bankReference\":\"" + topUpReference + "\",\"transferDate\":\"" + today
						+ "\",\"note\":\"Bravo " + companyToken + "\"}"),
				201);
		// The key is mandatory on wallet adjustments since B7 W1 (ADR 0022).
		HttpResponse<String> adjustment = send(request("/api/v1/ops/companies/" + w.companyB + "/wallet/adjustments")
			.header("Authorization", "Bearer " + ops.accessToken())
			.header("Content-Type", "application/json")
			.header("Idempotency-Key", UUID.randomUUID().toString())
			.POST(java.net.http.HttpRequest.BodyPublishers
				.ofString("{\"amountPiastres\":1000,\"reason\":\"Bravo " + companyToken + " goodwill\"}")));
		assertThat(adjustment.statusCode()).as(adjustment.body()).isBetween(200, 201);

		// A ticket on each side, answered by Operations with a reply and an internal note.
		String companyTicket = JsonPath.read(expect(authed("POST", "/api/v1/company/tickets", fleetB.accessToken(),
				"{\"type\":\"LOST_TAG\",\"subject\":\"Bravo " + companyToken + " lost tag\",\"body\":\"Lost\",\"vehicleId\":\""
						+ b1.vehicleId() + "\"}"),
				201).body(), "$.ticket.id");
		String operatorTicket = JsonPath.read(expect(authed("POST", "/api/v1/station/tickets",
				stationManagerB.accessToken(), "{\"type\":\"DEVICE_FAULT\",\"subject\":\"Bravo " + operatorToken
						+ " reader\",\"body\":\"Broken\",\"stationId\":\"" + stationB + "\"}"),
				201).body(), "$.ticket.id");
		for (String ticket : List.of(companyTicket, operatorTicket)) {
			expect(authed("POST", "/api/v1/ops/tickets/" + ticket + "/messages", ops.accessToken(), "{\"body\":\"On it\"}"),
					200);
			expect(authed("POST", "/api/v1/ops/tickets/" + ticket + "/messages", ops.accessToken(),
					"{\"body\":\"Bravo internal\",\"internal\":true}"), 200);
		}

		// A bulk import uploaded for company B.
		String importJob = id(upload(w.companyB, ("plate,make,model,fuel_type,tank_capacity_l\nBR "
				+ hex(6).toUpperCase() + ",Toyota,Hilux,GAS_92,60\n").getBytes(StandardCharsets.UTF_8)), 201);

		Map<String, String> b = w.b;
		b.put("company", w.companyB);
		b.put("department", departmentB);
		b.put("vehicle", b1.vehicleId());
		b.put("plate", Owner.queryOne("select plate from vehicles where id = ?::uuid", b1.vehicleId()));
		b.put("tag", Owner.<UUID>queryOne("select id from rfid_tags where vehicle_id = ?::uuid", b1.vehicleId()).toString());
		b.put("limit", JsonPath.<String>read(limit.body(), "$[0].id"));
		b.put("operator", w.operatorB);
		b.put("station", stationB);
		b.put("stationName", operatorToken);
		b.put("device", pumpB.deviceId());
		b.put("deviceCode", pumpB.deviceCode());
		b.put("agent", agentId(pumpB));
		b.put("workerId", pumpB.workerId());
		b.put("pin", pumpB.pin());
		b.put("agentSession", pumpB.session());
		b.put("fueling", id(fueled));
		b.put("clientTxnId", clientTxnId.toString());
		b.put("fuelingTag", b1.tagUid());
		b.put("heldFueling", fuelingId(synced, 0));
		b.put("batch", batch.toString());
		b.put("reversal", reversal);
		b.put("alert", Owner.<UUID>queryOne("select id from alerts where company_id = ?::uuid order by id limit 1", w.companyB)
			.toString());
		b.put("topup", Owner.<UUID>queryOne(
				"select id from topup_requests where company_id = ?::uuid and status = 'CONFIRMED'", w.companyB).toString());
		b.put("pendingTopup", pendingTopUp);
		b.put("topupReference", topUpReference);
		b.put("adjustment", JsonPath.<String>read(adjustment.body(), "$.id"));
		b.put("bankChange", bankChange);
		b.put("payout", payout);
		b.put("importJob", importJob);
		b.put("companyTicket", companyTicket);
		b.put("operatorTicket", operatorTicket);
		b.put("user", fleetB.userId().toString());
		b.put("session", Owner.<UUID>queryOne("select id from sessions where user_id = ? and revoked_at is null",
				fleetB.userId()).toString());
		// No tenant owns a reconciliation run (Operations' own record); a random id stands in for one.
		b.put("reconciliationRun", UUID.randomUUID().toString());

		Map<String, String> c = w.constants;
		c.put("today", today.toString());
		c.put("from", today.minusDays(1).toString());
		c.put("to", today.plusDays(1).toString());
		c.put("now", Instant.now().toString());
		c.put("a.session", w.pumpA.session());
		c.put("a.tag", carA.tagUid());
		c.put("a.agent", agentId(w.pumpA));
		c.put("a.txn", UUID.randomUUID().toString());
		c.put("a.batch", UUID.randomUUID().toString());
		c.put("a.receiptSig", receiptA.substring(receiptA.indexOf("sig=") + 4));
		c.put("a.publicKey", publicKey(w.pumpA.keys()));
		c.put("a.epoch", "0"); // replaced when sent: the device token request is signed at that moment
		c.put("a.nonce", "nonce-placeholder-" + hex(8));

		w.operatorMarkersB = markers(OPERATOR_MARKERS, w.operatorB, operatorToken);
		w.markersB = new LinkedHashSet<>(markers(COMPANY_MARKERS, w.companyB, companyToken));
		w.markersB.addAll(w.operatorMarkersB);
		return w;
	}

	private String company(String name) throws Exception {
		return id(authed("POST", "/api/v1/ops/companies", ops.accessToken(), "{\"company\":{\"legalName\":\"" + name
				+ "\",\"nameAr\":\"" + name + "\",\"nameEn\":\"" + name + "\"},\"lowBalanceThresholdPiastres\":0}"), 201);
	}

	private String department(String company, String name) throws Exception {
		return id(authed("POST", "/api/v1/ops/companies/" + company + "/departments", ops.accessToken(),
				"{\"name\":\"" + name + "\"}"), 201);
	}

	private String operatorNamed(String name) throws Exception {
		return id(authed("POST", "/api/v1/ops/operators", ops.accessToken(),
				"{\"legalName\":\"" + name + "\",\"nameAr\":\"" + name + "\",\"nameEn\":\"" + name + "\"}"), 201);
	}

	private String stationNamed(String operator, String name) throws Exception {
		return id(authed("POST", "/api/v1/ops/stations", ops.accessToken(), "{\"operatorId\":\"" + operator
				+ "\",\"station\":{\"nameAr\":\"" + name + "\",\"nameEn\":\"" + name
				+ "\",\"connectivity\":\"NORMAL\",\"fuelTypes\":[\"GAS_92\",\"DIESEL\"]}}"), 201);
	}

	/** A GAS_92 vehicle with an active tag, in a department when one is given. */
	private Car vehicle(String company, String department, long tankMl) throws Exception {
		String vehicle = id(authed("POST", "/api/v1/ops/vehicles", ops.accessToken(),
				"{\"companyId\":\"" + company + "\",\"vehicle\":{"
						+ (department == null ? "" : "\"departmentId\":\"" + department + "\",") + "\"plate\":\"SW "
						+ hex(8).toUpperCase() + "\",\"make\":\"Toyota\",\"model\":\"Hilux\",\"fuelType\":\"GAS_92\","
						+ "\"tankCapacityMl\":" + tankMl + ",\"tier\":\"NORMAL\"}}"),
				201);
		String uid = randomUid();
		expect(authed("POST", "/api/v1/ops/vehicles/" + vehicle + "/tags", ops.accessToken(), "{\"uid\":\"" + uid + "\"}"),
				201);
		return new Car(company, vehicle, uid);
	}

	private HttpResponse<String> upload(String company, byte[] content) throws Exception {
		String boundary = "----zawed" + UUID.randomUUID();
		ByteArrayOutputStream body = new ByteArrayOutputStream();
		body.write(("--" + boundary + "\r\nContent-Disposition: form-data; name=\"file\"; filename=\"fleet.csv\""
				+ "\r\nContent-Type: text/csv\r\n\r\n").getBytes(StandardCharsets.UTF_8));
		body.write(content);
		body.write(("\r\n--" + boundary + "--\r\n").getBytes(StandardCharsets.UTF_8));
		return send(request("/api/v1/ops/companies/" + company + "/fleet-imports")
			.header("Authorization", "Bearer " + ops.accessToken())
			.header("Content-Type", "multipart/form-data; boundary=" + boundary)
			.POST(HttpRequest.BodyPublishers.ofByteArray(body.toByteArray())));
	}

	private static HttpResponse<String> expect(HttpResponse<String> response, int status) {
		assertThat(response.statusCode()).as(response.body()).isEqualTo(status);
		return response;
	}

	private static String id(HttpResponse<String> response, int status) {
		return JsonPath.read(expect(response, status).body(), "$.id");
	}

	private static String id(HttpResponse<String> response) {
		return JsonPath.read(response.body(), "$.id");
	}

	// ------------------------------------------------------------------- markers and fingerprints

	/** Every value of company B's rows that an answer to A must never contain. {@code ?} is the company id. */
	private static final List<String> COMPANY_MARKERS = List.of("select id::text from companies where id = ?::uuid",
			"select id::text from departments where company_id = ?::uuid",
			"select id::text from vehicles where company_id = ?::uuid", "select plate from vehicles where company_id = ?::uuid",
			"select id::text from rfid_tags where company_id = ?::uuid",
			"select id::text from limit_rules where company_id = ?::uuid",
			"select id::text from topup_requests where company_id = ?::uuid",
			"select bank_reference from topup_requests where company_id = ?::uuid",
			"select id::text from wallet_adjustments where company_id = ?::uuid",
			"select id::text from ledger_accounts where company_id = ?::uuid",
			"select id::text from ledger_entries where company_id = ?::uuid",
			"select id::text from fleet_import_jobs where company_id = ?::uuid",
			"select id::text from fuelings where company_id = ?::uuid",
			"select client_txn_id::text from fuelings where company_id = ?::uuid",
			"select id::text from alerts where company_id = ?::uuid", "select id::text from tickets where company_id = ?::uuid",
			"select id::text from fueling_reversals where company_id = ?::uuid",
			"select id::text from users where scope_id = ?::uuid", "select email from users where scope_id = ?::uuid",
			"select s.id::text from sessions s join users u on u.id = s.user_id where u.scope_id = ?::uuid");

	/** The same for operator B. {@code ?} is the operator id. */
	private static final List<String> OPERATOR_MARKERS = List.of(
			"select id::text from station_operators where id = ?::uuid",
			"select id::text from stations where operator_id = ?::uuid",
			"select id::text from pos_devices where operator_id = ?::uuid",
			"select code from pos_devices where operator_id = ?::uuid", "select id::text from agents where operator_id = ?::uuid",
			"select worker_id from agents where operator_id = ?::uuid",
			"select full_name from agents where operator_id = ?::uuid",
			"select id::text from agent_sessions where operator_id = ?::uuid",
			"select id::text from station_bank_accounts where operator_id = ?::uuid",
			"select id::text from payouts where operator_id = ?::uuid",
			"select bank_reference from payouts where operator_id = ?::uuid",
			"select id::text from sync_batches where operator_id = ?::uuid",
			"select device_batch_id::text from sync_batches where operator_id = ?::uuid",
			"select id::text from fuelings where operator_id = ?::uuid",
			"select id::text from tickets where operator_id = ?::uuid",
			"select a.id::text from ledger_accounts a join stations s on s.id = a.station_id where s.operator_id = ?::uuid",
			"select id::text from users where scope_id = ?::uuid", "select email from users where scope_id = ?::uuid",
			"select s.id::text from sessions s join users u on u.id = s.user_id where u.scope_id = ?::uuid");

	private static Set<String> markers(List<String> queries, String tenant, String nameToken) {
		Set<String> markers = new LinkedHashSet<>();
		markers.add(nameToken);
		for (String sql : queries) {
			for (Object value : Owner.queryList(sql, tenant)) {
				if (value != null && value.toString().length() >= 6) {
					markers.add(value.toString());
				}
			}
		}
		return markers;
	}

	/**
	 * A digest of every row of tenant B, table by table. {@code %1$s} is the company, {@code %2$s} the
	 * operator; both are ids this test generated.
	 */
	private static final Map<String, String> TENANT_TABLES = new LinkedHashMap<>();

	static {
		String company = "company_id = '%1$s'";
		String either = "company_id = '%1$s' or operator_id = '%2$s'";
		String operator = "operator_id = '%2$s'";
		TENANT_TABLES.put("companies", "select * from companies where id = '%1$s'");
		for (String table : List.of("departments", "vehicles", "rfid_tags", "limit_rules", "company_wallets",
				"topup_requests", "wallet_adjustments", "ledger_entries", "fleet_import_jobs")) {
			TENANT_TABLES.put(table, "select * from " + table + " where " + company);
		}
		for (String table : List.of("tickets", "fuelings", "alerts", "fueling_reversals")) {
			TENANT_TABLES.put(table, "select * from " + table + " where " + either);
		}
		TENANT_TABLES.put("ticket_messages",
				"select m.* from ticket_messages m join tickets t on t.id = m.ticket_id where t." + company + " or t."
						+ operator);
		TENANT_TABLES.put("station_operators", "select * from station_operators where id = '%2$s'");
		for (String table : List.of("stations", "pos_devices", "agents", "agent_sessions", "station_payables",
				"station_bank_accounts", "payouts", "sync_batches", "payable_hold_releases")) {
			TENANT_TABLES.put(table, "select * from " + table + " where " + operator);
		}
		// Sessions and users without the columns their own sign-in and requests touch.
		TENANT_TABLES.put("sessions", "select s.id, s.revoked_at, s.revoked_reason from sessions s join users u on u.id = "
				+ "s.user_id where u.scope_id in ('%1$s', '%2$s')");
		TENANT_TABLES.put("users", "select id, status, role, scope_id, password_hash, failed_login_count, locked_until, "
				+ "mfa_enabled from users where scope_id in ('%1$s', '%2$s')");
	}

	private static Map<String, String> fingerprint(String company, String operator) {
		Map<String, String> digests = new TreeMap<>();
		TENANT_TABLES.forEach((table, sql) -> digests.put(table, Owner.<String>queryOne("""
				select count(*) || ':' || coalesce(md5(string_agg(row_to_json(t)::text, '|' order by row_to_json(t)::text)), '')
				from (%s) t
				""".formatted(sql.formatted(company, operator)))));
		return digests;
	}

	/** What tenant A's portal users have written: no scoped probe may add to it. */
	private static String ownWrites(World w) {
		return Owner.<String>queryOne("""
				select (select count(*) from tickets where company_id = ?::uuid or operator_id = ?::uuid) || '/'
				    || (select count(*) from ticket_messages m join tickets t on t.id = m.ticket_id
				        where t.company_id = ?::uuid or t.operator_id = ?::uuid) || '/'
				    || (select count(*) from topup_requests where company_id = ?::uuid) || '/'
				    || (select count(*) from sessions s join users u on u.id = s.user_id
				        where u.scope_id in (?::uuid, ?::uuid) and s.revoked_at is not null)
				""", w.companyA, w.operatorA, w.companyA, w.operatorA, w.companyA, w.companyA, w.operatorA);
	}

	/** What operator A's device has recorded, and whose sessions on it are live. */
	private static String deviceWrites(World w) {
		return Owner.<String>queryOne("""
				select (select count(*) from fuelings where device_id = ?::uuid) || '/'
				    || (select count(*) from agent_sessions where device_id = ?::uuid and revoked_at is null)
				""", w.pumpA.deviceId(), w.pumpA.deviceId());
	}

	// ------------------------------------------------------------------------------------ values

	private static String hex(int length) {
		StringBuilder out = new StringBuilder(length);
		ThreadLocalRandom random = ThreadLocalRandom.current();
		while (out.length() < length) {
			out.append(Character.forDigit(random.nextInt(16), 16));
		}
		return out.toString();
	}

	private static String digits(int length) {
		StringBuilder out = new StringBuilder(length);
		ThreadLocalRandom random = ThreadLocalRandom.current();
		while (out.length() < length) {
			out.append(random.nextInt(10));
		}
		return out.toString();
	}

}
