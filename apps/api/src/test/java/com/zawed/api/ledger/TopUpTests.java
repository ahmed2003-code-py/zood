package com.zawed.api.ledger;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.net.http.HttpResponse;
import java.time.LocalDate;
import java.util.List;
import java.util.UUID;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.test.context.TestPropertySource;

import com.zawed.api.identity.IdentityTestSupport;
import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.support.Concurrently;
import com.zawed.api.support.Owner;

/**
 * Top-up by bank transfer with maker-checker (V12, ADR 0021), end to end over HTTP: a company's claim
 * is never proof, Operations verifies what actually arrived, a different person approves, and only
 * the verified amount is credited, once.
 */
@TestPropertySource(properties = "zawed.finance.receiving-accounts=MAIN,CIB")
class TopUpTests extends IdentityTestSupport {

	private SignedIn ops;

	private SignedIn manager;

	@BeforeEach
	void signInOps() throws Exception {
		ops = signIn(seed(Role.OPS));
		manager = signIn(seed(Role.OPS_MANAGER));
	}

	@Test
	void theVerifiedAmountIsCreditedNeverTheClaimedOne() throws Exception {
		UUID company = seedCompany();
		SignedIn fleet = signIn(seed(Role.FLEET_MANAGER, Scope.company(company), false));
		String reference = reference();

		HttpResponse<String> requested = request(fleet, 250_000, reference);
		assertThat(requested.statusCode()).as(requested.body()).isEqualTo(201);
		assertThat(JsonPath.<String>read(requested.body(), "$.status")).isEqualTo("PENDING");
		String id = JsonPath.read(requested.body(), "$.id");
		assertThat(LedgerTests.cachedBalance(company)).as("a claim moves nothing").isZero();

		// The statement shows 240,000 arrived, not the 250,000 the company typed.
		HttpResponse<String> verified = verify(id, 240_000, "{\"evidenceReference\":\"Statement 2026-11 line 4\"");
		assertThat(verified.statusCode()).as(verified.body()).isEqualTo(200);
		assertThat(JsonPath.<String>read(verified.body(), "$.status")).isEqualTo("PENDING_APPROVAL");
		assertThat(JsonPath.<Integer>read(verified.body(), "$.verifiedAmountPiastres")).isEqualTo(240_000);
		assertThat(JsonPath.<String>read(verified.body(), "$.verifiedBy")).isEqualTo(ops.userId().toString());
		assertThat(LedgerTests.cachedBalance(company)).as("a verification moves nothing").isZero();

		HttpResponse<String> approved = approve(id, manager);
		assertThat(approved.statusCode()).as(approved.body()).isEqualTo(200);
		assertThat(JsonPath.<String>read(approved.body(), "$.status")).isEqualTo("CONFIRMED");
		assertThat(JsonPath.<String>read(approved.body(), "$.decidedBy")).isEqualTo(manager.userId().toString());
		assertThat(JsonPath.<Integer>read(approved.body(), "$.amountPiastres")).as("the claim is kept").isEqualTo(250_000);
		String txn = JsonPath.read(approved.body(), "$.ledgerTransactionId");

		assertThat(LedgerTests.cachedBalance(company)).isEqualTo(240_000);
		assertThat(LedgerTests.ledgerBalance(company)).isEqualTo(240_000);
		assertThat(Owner.<Long>queryOne("""
				select coalesce(sum(case direction when 'DEBIT' then amount_piastres else -amount_piastres end), 0)::bigint
				from ledger_entries where transaction_id = ?""", UUID.fromString(txn))).as("balanced").isZero();
		assertThat(Owner.<UUID>queryOne("select posted_by from ledger_transactions where id = ?", UUID.fromString(txn)))
			.isEqualTo(manager.userId());
		List<String> actions = Owner.queryList("select action from audit_log where scope_id = ? order by occurred_at, id",
				company);
		assertThat(actions).containsSubsequence("TOPUP_REQUESTED", "TOPUP_VERIFIED", "TOPUP_CONFIRMED");

		HttpResponse<String> statement = authed("GET", "/api/v1/company/wallet/statement", fleet.accessToken(), null);
		assertThat(JsonPath.<Integer>read(statement.body(), "$.items[0].amountPiastres")).isEqualTo(240_000);
		assertThat(JsonPath.<String>read(statement.body(), "$.items[0].sourceId")).isEqualTo(id);
	}

	@Test
	void theVerifierCannotApproveAndPlainOpsCannotApproveAtAll() throws Exception {
		UUID company = seedCompany();
		SignedIn fleet = signIn(seed(Role.FLEET_MANAGER, Scope.company(company), false));
		String id = JsonPath.read(request(fleet, 10_000, reference()).body(), "$.id");

		// A manager who verifies may not approve their own verification.
		HttpResponse<String> verified = authed("POST", "/api/v1/ops/topups/" + id + "/verify", manager.accessToken(),
				"{\"receivedAmountPiastres\":10000}");
		assertThat(verified.statusCode()).as(verified.body()).isEqualTo(200);
		assertThat(code(approve(id, manager))).isEqualTo("SELF_APPROVAL_NOT_ALLOWED");
		// An Ops user has no approval permission.
		HttpResponse<String> byOps = approve(id, ops);
		assertThat(byOps.statusCode()).isEqualTo(403);
		assertThat(code(byOps)).isEqualTo("FORBIDDEN");
		assertThat(LedgerTests.cachedBalance(company)).isZero();

		// A second manager may.
		assertThat(approve(id, signIn(seed(Role.OPS_MANAGER))).statusCode()).isEqualTo(200);
		assertThat(LedgerTests.cachedBalance(company)).isEqualTo(10_000);
	}

	@Test
	void theDatabaseRefusesASingleStepConfirmationWhateverTheCode() throws Exception {
		UUID company = seedCompany();
		SignedIn fleet = signIn(seed(Role.FLEET_MANAGER, Scope.company(company), false));
		String id = JsonPath.read(request(fleet, 10_000, reference()).body(), "$.id");
		verify(id, 10_000, "{");
		// Even the owner cannot mark it confirmed by its verifier: the two-person CHECK (V12).
		assertThatThrownBy(() -> Owner.update("""
				update topup_requests set status = 'CONFIRMED', decided_by = verified_by, decided_at = now(),
				       ledger_transaction_id = (select id from ledger_transactions limit 1)
				where id = ?""", UUID.fromString(id))).hasMessageContaining("topup_requests");
		// Nor rewrite what was verified.
		assertThatThrownBy(() -> Owner.update("update topup_requests set verified_amount_piastres = 99 where id = ?",
				UUID.fromString(id))).hasMessageContaining("written once");
	}

	@Test
	void aRepeatedApprovalCreditsOnce() throws Exception {
		UUID company = seedCompany();
		SignedIn fleet = signIn(seed(Role.FLEET_MANAGER, Scope.company(company), false));
		String id = JsonPath.read(request(fleet, 7_000, reference()).body(), "$.id");
		verify(id, 7_000, "{");
		assertThat(approve(id, manager).statusCode()).isEqualTo(200);
		HttpResponse<String> again = approve(id, manager);
		assertThat(again.statusCode()).as("a retry answers the confirmed top-up").isEqualTo(200);
		assertThat(JsonPath.<String>read(again.body(), "$.status")).isEqualTo("CONFIRMED");
		assertThat(LedgerTests.cachedBalance(company)).isEqualTo(7_000);
		assertThat(Owner.<Long>queryOne(
				"select count(*) from ledger_transactions where source_type = 'TOPUP' and source_id = ?", UUID.fromString(id)))
			.isEqualTo(1L);
	}

	@Test
	void racingApprovalsByDifferentManagersCreditOnce() throws Exception {
		UUID company = seedCompany();
		SignedIn fleet = signIn(seed(Role.FLEET_MANAGER, Scope.company(company), false));
		String id = JsonPath.read(request(fleet, 40_000, reference()).body(), "$.id");
		verify(id, 40_000, "{");
		List<SignedIn> managers = List.of(manager, signIn(seed(Role.OPS_MANAGER)), signIn(seed(Role.OPS_MANAGER)));

		List<Integer> statuses = Concurrently.run(3, (i) -> approve(id, managers.get(i)).statusCode());
		assertThat(statuses).allMatch((s) -> s == 200);
		assertThat(LedgerTests.cachedBalance(company)).isEqualTo(40_000);
		assertThat(LedgerTests.ledgerBalance(company)).isEqualTo(40_000);
		assertThat(Owner.<Long>queryOne(
				"select count(*) from ledger_transactions where source_type = 'TOPUP' and source_id = ?", UUID.fromString(id)))
			.isEqualTo(1L);
	}

	@Test
	void aRejectedTopUpIsNeverCreditedAndFreesItsReference() throws Exception {
		UUID company = seedCompany();
		SignedIn fleet = signIn(seed(Role.FLEET_MANAGER, Scope.company(company), false));
		String reference = reference();
		String id = JsonPath.read(request(fleet, 1_000, reference).body(), "$.id");

		assertThat(authed("POST", "/api/v1/ops/topups/" + id + "/reject", ops.accessToken(), "{\"reason\":\" \"}")
			.statusCode()).isEqualTo(400);
		verify(id, 1_000, "{");
		// The approver disagrees with the verification.
		HttpResponse<String> rejected = authed("POST", "/api/v1/ops/topups/" + id + "/reject", manager.accessToken(),
				"{\"reason\":\"Amount on the statement is for another company\"}");
		assertThat(JsonPath.<String>read(rejected.body(), "$.status")).as(rejected.body()).isEqualTo("REJECTED");
		assertThat(code(approve(id, manager))).isEqualTo("TOPUP_NOT_AWAITING_APPROVAL");
		assertThat(code(authed("POST", "/api/v1/company/topups/" + id + "/cancel", fleet.accessToken(), null)))
			.isEqualTo("TOPUP_NOT_PENDING");
		assertThat(LedgerTests.cachedBalance(company)).isZero();
		assertThat(request(fleet, 1_000, reference).statusCode()).as("the reference is free again").isEqualTo(201);
	}

	@Test
	void theCompanyMayCancelOnlyBeforeVerification() throws Exception {
		UUID company = seedCompany();
		SignedIn accountant = signIn(seed(Role.ACCOUNTANT, Scope.company(company), false));
		String first = JsonPath.read(request(accountant, 1_000, reference()).body(), "$.id");
		HttpResponse<String> cancelled = authed("POST", "/api/v1/company/topups/" + first + "/cancel",
				accountant.accessToken(), null);
		assertThat(JsonPath.<String>read(cancelled.body(), "$.status")).isEqualTo("CANCELLED");
		assertThat(code(verify(first, 1_000, "{"))).isEqualTo("TOPUP_NOT_PENDING");

		String second = JsonPath.read(request(accountant, 2_000, reference()).body(), "$.id");
		verify(second, 2_000, "{");
		assertThat(code(authed("POST", "/api/v1/company/topups/" + second + "/cancel", accountant.accessToken(), null)))
			.isEqualTo("TOPUP_NOT_PENDING");
	}

	@Test
	void aReferenceIsUniquePerReceivingAccount() throws Exception {
		UUID company = seedCompany();
		UUID other = seedCompany();
		SignedIn fleet = signIn(seed(Role.FLEET_MANAGER, Scope.company(company), false));
		SignedIn otherFleet = signIn(seed(Role.FLEET_MANAGER, Scope.company(other), false));
		String reference = "trx 100-" + UUID.randomUUID().toString().substring(0, 6);
		String id = JsonPath.read(request(fleet, 5_000, reference).body(), "$.id");
		verify(id, 5_000, "{");
		approve(id, manager);

		// The same transfer, respelled, on the same account: refused, whoever asks and however.
		String respelled = " TRX100-" + reference.substring(8).toUpperCase() + " ";
		assertThat(code(request(otherFleet, 5_000, respelled))).isEqualTo("BANK_REFERENCE_IN_USE");
		assertThat(code(record(other, 5_000, respelled, "MAIN"))).isEqualTo("BANK_REFERENCE_IN_USE");
		// The same reference text on another ZAWED account is another transfer.
		HttpResponse<String> elsewhere = record(other, 5_000, respelled, "cib");
		assertThat(elsewhere.statusCode()).as(elsewhere.body()).isEqualTo(201);
		assertThat(JsonPath.<String>read(elsewhere.body(), "$.receivingAccount")).isEqualTo("CIB");
		assertThat(code(record(other, 5_000, reference(), "NOPE"))).isEqualTo("UNKNOWN_RECEIVING_ACCOUNT");
		assertThat(LedgerTests.cachedBalance(other)).isZero();
	}

	@Test
	void recordingTheSameTransferTwiceAnswersTheFirstRecord() throws Exception {
		UUID company = seedCompany();
		String reference = reference();
		HttpResponse<String> first = record(company, 30_000, reference, null);
		assertThat(first.statusCode()).as(first.body()).isEqualTo(201);
		assertThat(JsonPath.<String>read(first.body(), "$.status")).isEqualTo("PENDING_APPROVAL");
		assertThat(JsonPath.<String>read(first.body(), "$.origin")).isEqualTo("OPS_RECORDED");
		HttpResponse<String> retry = record(company, 30_000, reference, null);
		assertThat(retry.statusCode()).isEqualTo(200);
		assertThat(JsonPath.<String>read(retry.body(), "$.id")).isEqualTo(JsonPath.<String>read(first.body(), "$.id"));
		assertThat(code(record(company, 31_000, reference, null))).as("same reference, another amount")
			.isEqualTo("BANK_REFERENCE_IN_USE");
		assertThat(LedgerTests.cachedBalance(company)).as("recorded, not approved").isZero();
	}

	@Test
	void retriesOfOneRecordedTransferRacedWithoutAnyKeyAreOneTopUpAndCreditOnce() throws Exception {
		// Why recording a transfer needs no Idempotency-Key (review finding 1): the transfer itself is the
		// key. Its reference is unique per receiving account among live top-ups, and the insert answers a
		// conflict by reading the first record, so retries raced at once are one top-up.
		UUID company = seedCompany();
		String reference = reference();
		List<HttpResponse<String>> answers = Concurrently.run(4, (i) -> record(company, 12_000, reference, null));
		assertThat(answers).extracting(HttpResponse::statusCode).containsOnlyOnce(201).containsOnly(201, 200);
		List<String> ids = answers.stream().map((r) -> JsonPath.<String>read(r.body(), "$.id")).toList();
		assertThat(ids).containsOnly(ids.getFirst());
		assertThat(Owner.<Long>queryOne("select count(*) from topup_requests where company_id = ?", company)).isEqualTo(1L);
		for (String id : ids) {
			assertThat(approve(id, manager).statusCode()).isEqualTo(200);
		}
		assertThat(LedgerTests.cachedBalance(company)).isEqualTo(12_000);
		assertThat(LedgerTests.ledgerBalance(company)).isEqualTo(12_000);
	}

	@Test
	void racingRequestsWithOneReferenceAcceptOne() throws Exception {
		UUID company = seedCompany();
		SignedIn fleet = signIn(seed(Role.FLEET_MANAGER, Scope.company(company), false));
		String reference = reference();
		List<Integer> statuses = Concurrently.run(2, (i) -> request(fleet, 1_000 + i, reference).statusCode());
		assertThat(statuses).containsExactlyInAnyOrder(201, 409);
	}

	@Test
	void verificationMayCorrectTheReferenceToWhatTheStatementSays() throws Exception {
		UUID company = seedCompany();
		SignedIn fleet = signIn(seed(Role.FLEET_MANAGER, Scope.company(company), false));
		String typed = reference();
		String onStatement = reference();
		String id = JsonPath.read(request(fleet, 2_000, typed).body(), "$.id");
		HttpResponse<String> verified = verify(id, 2_000, "{\"bankReference\":\"" + onStatement + "\"");
		assertThat(JsonPath.<String>read(verified.body(), "$.bankReference")).isEqualTo(onStatement);
		approve(id, manager);
		assertThat(Owner.<String>queryOne("select reference from ledger_transactions where source_id = ?", UUID.fromString(id)))
			.isEqualTo(onStatement);
		assertThat(request(fleet, 2_000, typed).statusCode()).isEqualTo(201);
		assertThat(code(request(fleet, 2_000, onStatement))).isEqualTo("BANK_REFERENCE_IN_USE");
	}

	@Test
	void aTransferDateInTheFutureABlankReferenceAndZeroAreRefused() throws Exception {
		UUID company = seedCompany();
		SignedIn fleet = signIn(seed(Role.FLEET_MANAGER, Scope.company(company), false));
		assertThat(code(authed("POST", "/api/v1/company/topups", fleet.accessToken(),
				body(1_000, reference(), LocalDate.now().plusDays(2))))).isEqualTo("TRANSFER_DATE_IN_FUTURE");
		assertThat(authed("POST", "/api/v1/company/topups", fleet.accessToken(), body(1_000, "   ", LocalDate.now()))
			.statusCode()).isEqualTo(400);
		assertThat(authed("POST", "/api/v1/company/topups", fleet.accessToken(), body(0, reference(), LocalDate.now()))
			.statusCode()).isEqualTo(400);
		String id = JsonPath.read(request(fleet, 1_000, reference()).body(), "$.id");
		assertThat(authed("POST", "/api/v1/ops/topups/" + id + "/verify", ops.accessToken(), "{\"receivedAmountPiastres\":0}")
			.statusCode()).isEqualTo(400);
	}

	@Test
	void aCompanySeesOnlyItsOwnWalletAndTopUps() throws Exception {
		UUID mine = seedCompany();
		UUID theirs = seedCompany();
		SignedIn me = signIn(seed(Role.ACCOUNTANT, Scope.company(mine), false));
		SignedIn them = signIn(seed(Role.FLEET_MANAGER, Scope.company(theirs), false));
		String theirRequest = JsonPath.read(request(them, 3_000, reference()).body(), "$.id");
		List<String> visible = JsonPath.read(authed("GET", "/api/v1/company/topups?limit=100", me.accessToken(), null).body(),
				"$.items[*].id");
		assertThat(visible).doesNotContain(theirRequest);
		assertThat(authed("POST", "/api/v1/company/topups/" + theirRequest + "/cancel", me.accessToken(), null).statusCode())
			.isEqualTo(404);
		assertThat(authed("GET", "/api/v1/ops/companies/" + UUID.randomUUID() + "/wallet", ops.accessToken(), null)
			.statusCode()).isEqualTo(404);
	}

	@Test
	void theStatementPagesNewestFirstWithRunningBalances() throws Exception {
		UUID company = seedCompany();
		for (int i = 1; i <= 3; i++) {
			fund(ops, company, 1_000L * i);
		}
		HttpResponse<String> first = authed("GET", "/api/v1/ops/companies/" + company + "/wallet/statement?limit=2",
				ops.accessToken(), null);
		assertThat(JsonPath.<List<Integer>>read(first.body(), "$.items[*].balanceAfterPiastres")).containsExactly(6_000, 3_000);
		String cursor = JsonPath.read(first.body(), "$.nextCursor");
		HttpResponse<String> second = authed("GET",
				"/api/v1/ops/companies/" + company + "/wallet/statement?limit=2&cursor=" + cursor, ops.accessToken(), null);
		assertThat(JsonPath.<List<Integer>>read(second.body(), "$.items[*].balanceAfterPiastres")).containsExactly(1_000);
		assertThat(code(authed("GET", "/api/v1/ops/companies/" + company + "/wallet/statement?cursor=abc", ops.accessToken(),
				null))).isEqualTo("INVALID_CURSOR");
	}

	@Test
	void aPastedVariantOfAReferenceIsTheSameTransfer() throws Exception {
		UUID company = seedCompany();
		SignedIn fleet = signIn(seed(Role.FLEET_MANAGER, Scope.company(company), false));
		String digits = UUID.randomUUID().toString().replaceAll("[^0-9]", "").concat("123456").substring(0, 6);
		assertThat(request(fleet, 10_000, "FT" + digits).statusCode()).isEqualTo(201);
		for (String variant : List.of("FT‏" + digits, "FT " + digits, "ft-" + digits, "FT" + arabic(digits),
				"ＦＴ" + digits, " F.T " + digits + " ")) {
			assertThat(code(request(fleet, 10_000, variant))).as(variant).isEqualTo("BANK_REFERENCE_IN_USE");
		}
		assertThat(code(request(fleet, 10_000, "- ‏ ."))).isEqualTo("INVALID_BANK_REFERENCE");
	}

	@Test
	void theReferenceKeyIsCanonical() {
		assertThat(TopUps.referenceKey(" ft-١٢٣ ")).isEqualTo("FT123");
		assertThat(TopUps.referenceKey("FT‏۱۲۳")).isEqualTo("FT123");
		assertThat(TopUps.referenceKey("ＦＴ１２３")).isEqualTo("FT123");
		assertThat(TopUps.referenceKey("FT 123")).isEqualTo("FT123");
		assertThat(TopUps.referenceKey("‏-")).isEmpty();
	}

	// ----------------------------------------------------------------------------------- helpers

	/** Operations verifies; {@code extra} is the start of a JSON object with any other fields. */
	private HttpResponse<String> verify(String id, long received, String extra) throws Exception {
		String json = (extra.equals("{") ? "{" : extra + ",") + "\"receivedAmountPiastres\":" + received + "}";
		return authed("POST", "/api/v1/ops/topups/" + id + "/verify", ops.accessToken(), json);
	}

	private HttpResponse<String> approve(String id, SignedIn who) throws Exception {
		return authed("POST", "/api/v1/ops/topups/" + id + "/approve", who.accessToken(), null);
	}

	private HttpResponse<String> record(UUID company, long amount, String reference, String account) throws Exception {
		return authed("POST", "/api/v1/ops/companies/" + company + "/wallet/credits", ops.accessToken(),
				"{\"amountPiastres\":" + amount + ",\"bankReference\":\"" + reference + "\",\"transferDate\":\""
						+ LocalDate.now().minusDays(1) + "\"" + (account == null ? "" : ",\"receivingAccount\":\"" + account + "\"")
						+ "}");
	}

	private HttpResponse<String> request(SignedIn user, long amount, String reference) throws Exception {
		return authed("POST", "/api/v1/company/topups", user.accessToken(), body(amount, reference, LocalDate.now()));
	}

	private static String body(long amount, String reference, LocalDate date) {
		return "{\"amountPiastres\":" + amount + ",\"bankReference\":\"" + reference + "\",\"transferDate\":\"" + date + "\"}";
	}

	private static String reference() {
		return "REF-" + UUID.randomUUID().toString().substring(0, 12).toUpperCase();
	}

	private static String arabic(String digits) {
		StringBuilder out = new StringBuilder();
		digits.chars().forEach((c) -> out.append((char) ('٠' + (c - '0'))));
		return out.toString();
	}

}
