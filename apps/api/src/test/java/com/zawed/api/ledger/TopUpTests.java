package com.zawed.api.ledger;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.time.LocalDate;
import java.util.List;
import java.util.UUID;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;

import com.zawed.api.identity.IdentityTestSupport;
import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.support.Concurrently;
import com.zawed.api.support.Owner;

/** Top-up by bank transfer, end to end over HTTP (PRD §4.6, §6.3), and the company's view of its wallet. */
class TopUpTests extends IdentityTestSupport {

	private SignedIn ops;

	@BeforeEach
	void signInOps() throws Exception {
		ops = signIn(seed(Role.OPS));
	}

	@Test
	void aFleetManagerRequestsAndOpsConfirmsIntoTheWallet() throws Exception {
		UUID company = seedCompany();
		SignedIn manager = signIn(seed(Role.FLEET_MANAGER, Scope.company(company), false));
		String reference = reference();

		HttpResponse<String> requested = request(manager, 250_000, reference);
		assertThat(requested.statusCode()).as(requested.body()).isEqualTo(201);
		assertThat(JsonPath.<String>read(requested.body(), "$.status")).isEqualTo("PENDING");
		assertThat(JsonPath.<String>read(requested.body(), "$.requestedBy")).isEqualTo(manager.userId().toString());
		String id = JsonPath.read(requested.body(), "$.id");

		List<String> queue = JsonPath.read(authed("GET", "/api/v1/ops/topups?status=PENDING&limit=100", ops.accessToken(), null)
			.body(), "$.items[*].id");
		assertThat(queue).contains(id);

		HttpResponse<String> confirmed = authed("POST", "/api/v1/ops/topups/" + id + "/confirm", ops.accessToken(), "{}");
		assertThat(confirmed.statusCode()).as(confirmed.body()).isEqualTo(200);
		assertThat(JsonPath.<String>read(confirmed.body(), "$.status")).isEqualTo("CONFIRMED");
		assertThat(JsonPath.<String>read(confirmed.body(), "$.decidedBy")).isEqualTo(ops.userId().toString());
		String txn = JsonPath.read(confirmed.body(), "$.ledgerTransactionId");
		assertThat(txn).isNotNull();

		HttpResponse<String> wallet = authed("GET", "/api/v1/company/wallet", manager.accessToken(), null);
		assertThat(wallet.statusCode()).isEqualTo(200);
		assertThat(JsonPath.<Integer>read(wallet.body(), "$.balancePiastres")).isEqualTo(250_000);
		assertThat(JsonPath.<Boolean>read(wallet.body(), "$.lowBalance")).isFalse();

		HttpResponse<String> statement = authed("GET", "/api/v1/company/wallet/statement", manager.accessToken(), null);
		assertThat(statement.statusCode()).isEqualTo(200);
		assertThat(JsonPath.<String>read(statement.body(), "$.items[0].direction")).isEqualTo("CREDIT");
		assertThat(JsonPath.<Integer>read(statement.body(), "$.items[0].amountPiastres")).isEqualTo(250_000);
		assertThat(JsonPath.<Integer>read(statement.body(), "$.items[0].balanceAfterPiastres")).isEqualTo(250_000);
		assertThat(JsonPath.<String>read(statement.body(), "$.items[0].sourceType")).isEqualTo("TOPUP");
		assertThat(JsonPath.<String>read(statement.body(), "$.items[0].sourceId")).isEqualTo(id);
		assertThat(JsonPath.<String>read(statement.body(), "$.items[0].reference")).isEqualTo(reference);

		// PRD 4.6 step 5: the credit carries the Operations user and the payment reference.
		assertThat(Owner.<UUID>queryOne("select posted_by from ledger_transactions where id = ?", UUID.fromString(txn)))
			.isEqualTo(ops.userId());
		List<String> actions = Owner.queryList("select action from audit_log where scope_id = ? order by occurred_at, id",
				company);
		assertThat(actions).containsSubsequence("TOPUP_REQUESTED", "TOPUP_CONFIRMED");
	}

	@Test
	void anAccountantMayRequestAndCancelAndACancelledRequestCannotBeConfirmed() throws Exception {
		UUID company = seedCompany();
		SignedIn accountant = signIn(seed(Role.ACCOUNTANT, Scope.company(company), false));
		String reference = reference();
		String id = JsonPath.read(request(accountant, 1_000, reference).body(), "$.id");

		HttpResponse<String> cancelled = authed("POST", "/api/v1/company/topups/" + id + "/cancel", accountant.accessToken(), null);
		assertThat(cancelled.statusCode()).as(cancelled.body()).isEqualTo(200);
		assertThat(JsonPath.<String>read(cancelled.body(), "$.status")).isEqualTo("CANCELLED");

		HttpResponse<String> confirm = authed("POST", "/api/v1/ops/topups/" + id + "/confirm", ops.accessToken(), "{}");
		assertThat(confirm.statusCode()).isEqualTo(409);
		assertThat(code(confirm)).isEqualTo("TOPUP_NOT_PENDING");
		assertThat(LedgerTests.cachedBalance(company)).isZero();

		// Cancelling frees the reference for a corrected request.
		assertThat(request(accountant, 1_000, reference).statusCode()).isEqualTo(201);
		List<String> mine = JsonPath.read(authed("GET", "/api/v1/company/topups", accountant.accessToken(), null).body(),
				"$.items[*].status");
		assertThat(mine).containsExactlyInAnyOrder("CANCELLED", "PENDING");
	}

	@Test
	void theSameBankReferenceNeverCreditsTwice() throws Exception {
		UUID company = seedCompany();
		UUID other = seedCompany();
		SignedIn manager = signIn(seed(Role.FLEET_MANAGER, Scope.company(company), false));
		SignedIn otherManager = signIn(seed(Role.FLEET_MANAGER, Scope.company(other), false));
		String reference = "trx 100-" + UUID.randomUUID().toString().substring(0, 6);
		String id = JsonPath.read(request(manager, 5_000, reference).body(), "$.id");
		assertThat(authed("POST", "/api/v1/ops/topups/" + id + "/confirm", ops.accessToken(), "{}").statusCode()).isEqualTo(200);

		// Spelled differently, by the same or another company, requested or credited directly: refused.
		String respelled = " TRX100-" + reference.substring(8).toUpperCase() + " ";
		HttpResponse<String> again = request(manager, 5_000, respelled);
		assertThat(again.statusCode()).isEqualTo(409);
		assertThat(code(again)).isEqualTo("BANK_REFERENCE_IN_USE");
		assertThat(code(request(otherManager, 5_000, respelled))).isEqualTo("BANK_REFERENCE_IN_USE");
		HttpResponse<String> direct = authed("POST", "/api/v1/ops/companies/" + other + "/wallet/credits", ops.accessToken(),
				body(5_000, respelled, LocalDate.now()));
		assertThat(direct.statusCode()).isEqualTo(409);
		assertThat(code(direct)).isEqualTo("BANK_REFERENCE_IN_USE");

		assertThat(LedgerTests.cachedBalance(company)).isEqualTo(5_000);
		assertThat(LedgerTests.cachedBalance(other)).isZero();
		assertThat(Owner.<Long>queryOne("select count(*) from ledger_transactions where company_id = ?", other)).isZero();
	}

	@Test
	void racingConfirmationsCreditOnce() throws Exception {
		UUID company = seedCompany();
		SignedIn manager = signIn(seed(Role.FLEET_MANAGER, Scope.company(company), false));
		String id = JsonPath.read(request(manager, 40_000, reference()).body(), "$.id");

		List<Integer> statuses = Concurrently.run(3,
				(i) -> authed("POST", "/api/v1/ops/topups/" + id + "/confirm", ops.accessToken(), "{}").statusCode());
		assertThat(statuses).containsExactlyInAnyOrder(200, 409, 409);
		assertThat(LedgerTests.cachedBalance(company)).isEqualTo(40_000);
		assertThat(LedgerTests.ledgerBalance(company)).isEqualTo(40_000);
		assertThat(Owner.<Long>queryOne(
				"select count(*) from ledger_transactions where source_type = 'TOPUP' and source_id = ?", UUID.fromString(id)))
			.isEqualTo(1L);
	}

	@Test
	void racingRequestsWithOneReferenceAcceptOne() throws Exception {
		UUID company = seedCompany();
		SignedIn manager = signIn(seed(Role.FLEET_MANAGER, Scope.company(company), false));
		String reference = reference();
		List<Integer> statuses = Concurrently.run(2, (i) -> request(manager, 1_000 + i, reference).statusCode());
		assertThat(statuses).containsExactlyInAnyOrder(201, 409);
	}

	@Test
	void opsCreditsAWalletDirectlyWithAReference() throws Exception {
		UUID company = seedCompany(100_000);
		SignedIn accountant = signIn(seed(Role.ACCOUNTANT, Scope.company(company), false));
		assertThat(JsonPath.<Boolean>read(authed("GET", "/api/v1/company/wallet", accountant.accessToken(), null).body(),
				"$.lowBalance")).isTrue();

		HttpResponse<String> credited = authed("POST", "/api/v1/ops/companies/" + company + "/wallet/credits",
				ops.accessToken(), body(300_000, reference(), LocalDate.now().minusDays(1)));
		assertThat(credited.statusCode()).as(credited.body()).isEqualTo(201);
		assertThat(JsonPath.<String>read(credited.body(), "$.status")).isEqualTo("CONFIRMED");
		assertThat(JsonPath.<Object>read(credited.body(), "$.requestedBy")).isNull();
		assertThat(JsonPath.<String>read(credited.body(), "$.decidedBy")).isEqualTo(ops.userId().toString());

		HttpResponse<String> wallet = authed("GET", "/api/v1/company/wallet", accountant.accessToken(), null);
		assertThat(JsonPath.<Integer>read(wallet.body(), "$.balancePiastres")).isEqualTo(300_000);
		assertThat(JsonPath.<Boolean>read(wallet.body(), "$.lowBalance")).isFalse();
		List<String> mine = JsonPath.read(authed("GET", "/api/v1/company/topups", accountant.accessToken(), null).body(),
				"$.items[*].status");
		assertThat(mine).containsExactly("CONFIRMED");
		assertThat(Owner.<Long>queryOne(
				"select count(*) from audit_log where action = 'WALLET_CREDITED' and scope_id = ?", company)).isEqualTo(1L);
	}

	@Test
	void rejectionNeedsAReasonAndFreesTheReference() throws Exception {
		UUID company = seedCompany();
		SignedIn manager = signIn(seed(Role.FLEET_MANAGER, Scope.company(company), false));
		String reference = reference();
		String id = JsonPath.read(request(manager, 1_000, reference).body(), "$.id");

		assertThat(authed("POST", "/api/v1/ops/topups/" + id + "/reject", ops.accessToken(), "{\"reason\":\" \"}")
			.statusCode()).isEqualTo(400);
		HttpResponse<String> rejected = authed("POST", "/api/v1/ops/topups/" + id + "/reject", ops.accessToken(),
				"{\"reason\":\"No such transfer on the statement\"}");
		assertThat(rejected.statusCode()).as(rejected.body()).isEqualTo(200);
		assertThat(JsonPath.<String>read(rejected.body(), "$.status")).isEqualTo("REJECTED");
		assertThat(JsonPath.<String>read(rejected.body(), "$.decisionReason")).isEqualTo("No such transfer on the statement");
		assertThat(code(authed("POST", "/api/v1/ops/topups/" + id + "/reject", ops.accessToken(), "{\"reason\":\"again\"}")))
			.isEqualTo("TOPUP_NOT_PENDING");
		assertThat(code(authed("POST", "/api/v1/company/topups/" + id + "/cancel", manager.accessToken(), null)))
			.isEqualTo("TOPUP_NOT_PENDING");
		assertThat(request(manager, 1_000, reference).statusCode()).isEqualTo(201);
		assertThat(LedgerTests.cachedBalance(company)).isZero();
	}

	@Test
	void confirmationMayCorrectTheReferenceToWhatTheStatementSays() throws Exception {
		UUID company = seedCompany();
		SignedIn manager = signIn(seed(Role.FLEET_MANAGER, Scope.company(company), false));
		String typed = reference();
		String onStatement = reference();
		String id = JsonPath.read(request(manager, 2_000, typed).body(), "$.id");

		HttpResponse<String> confirmed = authed("POST", "/api/v1/ops/topups/" + id + "/confirm", ops.accessToken(),
				"{\"bankReference\":\"" + onStatement + "\",\"note\":\"Statement line 12\"}");
		assertThat(confirmed.statusCode()).as(confirmed.body()).isEqualTo(200);
		assertThat(JsonPath.<String>read(confirmed.body(), "$.bankReference")).isEqualTo(onStatement);
		assertThat(JsonPath.<String>read(confirmed.body(), "$.decisionReason")).isEqualTo("Statement line 12");
		assertThat(Owner.<String>queryOne("select reference from ledger_transactions where source_id = ?", UUID.fromString(id)))
			.isEqualTo(onStatement);
		// The typed reference is free again; the corrected one is taken.
		assertThat(request(manager, 2_000, typed).statusCode()).isEqualTo(201);
		assertThat(code(request(manager, 2_000, onStatement))).isEqualTo("BANK_REFERENCE_IN_USE");
	}

	@Test
	void aTransferDateInTheFutureAndABlankReferenceAreRefused() throws Exception {
		UUID company = seedCompany();
		SignedIn manager = signIn(seed(Role.FLEET_MANAGER, Scope.company(company), false));
		HttpResponse<String> future = authed("POST", "/api/v1/company/topups", manager.accessToken(),
				body(1_000, reference(), LocalDate.now().plusDays(2)));
		assertThat(future.statusCode()).isEqualTo(400);
		assertThat(code(future)).isEqualTo("TRANSFER_DATE_IN_FUTURE");
		HttpResponse<String> blank = authed("POST", "/api/v1/company/topups", manager.accessToken(),
				body(1_000, "   ", LocalDate.now()));
		assertThat(blank.statusCode()).isEqualTo(400);
		HttpResponse<String> zero = authed("POST", "/api/v1/company/topups", manager.accessToken(),
				body(0, reference(), LocalDate.now()));
		assertThat(zero.statusCode()).isEqualTo(400);
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
		assertThat(JsonPath.<String>read(authed("GET", "/api/v1/company/wallet", me.accessToken(), null).body(), "$.companyId"))
			.isEqualTo(mine.toString());
		// Ops: an unknown company's wallet is a 404, like any row outside a scope.
		assertThat(authed("GET", "/api/v1/ops/companies/" + UUID.randomUUID() + "/wallet", ops.accessToken(), null)
			.statusCode()).isEqualTo(404);
		assertThat(authed("GET", "/api/v1/ops/topups/" + UUID.randomUUID(), ops.accessToken(), null).statusCode())
			.isEqualTo(404);
	}

	@Test
	void theStatementPagesNewestFirstWithRunningBalances() throws Exception {
		UUID company = seedCompany();
		for (int i = 1; i <= 3; i++) {
			HttpResponse<String> credited = authed("POST", "/api/v1/ops/companies/" + company + "/wallet/credits",
					ops.accessToken(), body(1_000L * i, reference(), LocalDate.now()));
			assertThat(credited.statusCode()).as(credited.body()).isEqualTo(201);
		}
		HttpResponse<String> first = authed("GET", "/api/v1/ops/companies/" + company + "/wallet/statement?limit=2",
				ops.accessToken(), null);
		assertThat(first.statusCode()).isEqualTo(200);
		assertThat(JsonPath.<List<Integer>>read(first.body(), "$.items[*].balanceAfterPiastres")).containsExactly(6_000, 3_000);
		String cursor = JsonPath.read(first.body(), "$.nextCursor");
		assertThat(cursor).isNotNull();
		HttpResponse<String> second = authed("GET",
				"/api/v1/ops/companies/" + company + "/wallet/statement?limit=2&cursor=" + cursor, ops.accessToken(), null);
		assertThat(JsonPath.<List<Integer>>read(second.body(), "$.items[*].balanceAfterPiastres")).containsExactly(1_000);
		assertThat(JsonPath.<Object>read(second.body(), "$.nextCursor")).isNull();
		assertThat(code(authed("GET", "/api/v1/ops/companies/" + company + "/wallet/statement?cursor=abc", ops.accessToken(),
				null))).isEqualTo("INVALID_CURSOR");
	}

	// ----------------------------------------------------------------------------------- helpers

	private HttpResponse<String> request(SignedIn user, long amount, String reference) throws Exception {
		return authed("POST", "/api/v1/company/topups", user.accessToken(), body(amount, reference, LocalDate.now()));
	}

	private static String body(long amount, String reference, LocalDate date) {
		return "{\"amountPiastres\":" + amount + ",\"bankReference\":\"" + reference + "\",\"transferDate\":\"" + date + "\"}";
	}

	private static String reference() {
		return "REF-" + UUID.randomUUID().toString().substring(0, 12).toUpperCase();
	}

}
