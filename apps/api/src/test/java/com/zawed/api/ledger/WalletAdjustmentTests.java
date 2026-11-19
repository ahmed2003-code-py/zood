package com.zawed.api.ledger;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.util.List;
import java.util.UUID;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;

import com.zawed.api.identity.IdentityTestSupport;
import com.zawed.api.platform.security.Role;
import com.zawed.api.support.Concurrently;
import com.zawed.api.support.Owner;

/**
 * Wallet adjustments (V12, ADR 0021): never a shortcut. One person asks with a reason, a different
 * person approves, the ledger posts once against platform:adjustments, and an Idempotency-Key makes a
 * retried request safe.
 */
class WalletAdjustmentTests extends IdentityTestSupport {

	private SignedIn ops;

	private SignedIn manager;

	@BeforeEach
	void signIn() throws Exception {
		ops = signIn(seed(Role.OPS));
		manager = signIn(seed(Role.OPS_MANAGER));
	}

	@Test
	void anAdjustmentNeedsASecondPersonAndPostsAgainstTheAdjustmentAccount() throws Exception {
		UUID company = seedCompany();
		HttpResponse<String> requested = ask(ops, company, 15_000, "Goodwill for a faulty pump", null);
		assertThat(requested.statusCode()).as(requested.body()).isEqualTo(201);
		assertThat(JsonPath.<String>read(requested.body(), "$.status")).isEqualTo("PENDING_APPROVAL");
		String id = JsonPath.read(requested.body(), "$.id");
		assertThat(LedgerTests.cachedBalance(company)).isZero();

		assertThat(approve(id, ops).statusCode()).as("Ops has no approval permission").isEqualTo(403);
		HttpResponse<String> approved = approve(id, manager);
		assertThat(JsonPath.<String>read(approved.body(), "$.status")).as(approved.body()).isEqualTo("APPROVED");
		assertThat(LedgerTests.cachedBalance(company)).isEqualTo(15_000);
		assertThat(LedgerTests.ledgerBalance(company)).isEqualTo(15_000);
		String txn = JsonPath.read(approved.body(), "$.ledgerTransactionId");
		assertThat(Owner.<String>queryOne("select source_type from ledger_transactions where id = ?", UUID.fromString(txn)))
			.isEqualTo("ADJUSTMENT");
		List<String> accounts = Owner.queryList("""
				select a.code from ledger_entries e join ledger_accounts a on a.id = e.account_id
				where e.transaction_id = ? order by e.seq""", UUID.fromString(txn));
		assertThat(accounts).containsExactly("platform:adjustments", "company:" + company + ":wallet");

		// A repeat answers it as it is; nothing posts twice.
		assertThat(approve(id, manager).statusCode()).isEqualTo(200);
		assertThat(Owner.<Long>queryOne("select count(*) from ledger_transactions where source_id = ?", UUID.fromString(id)))
			.isEqualTo(1L);
	}

	@Test
	void theRequesterNeverApprovesTheirOwnAdjustment() throws Exception {
		UUID company = seedCompany();
		String id = JsonPath.read(ask(manager, company, 1_000, "Correction", null).body(), "$.id");
		assertThat(code(approve(id, manager))).isEqualTo("SELF_APPROVAL_NOT_ALLOWED");
		assertThat(LedgerTests.cachedBalance(company)).isZero();
		// Only the requester withdraws it.
		assertThat(code(authed("POST", "/api/v1/ops/wallet-adjustments/" + id + "/cancel", ops.accessToken(), null)))
			.isEqualTo("FORBIDDEN");
		HttpResponse<String> cancelled = authed("POST", "/api/v1/ops/wallet-adjustments/" + id + "/cancel",
				manager.accessToken(), null);
		assertThat(JsonPath.<String>read(cancelled.body(), "$.status")).isEqualTo("CANCELLED");
		assertThat(code(approve(id, signIn(seed(Role.OPS_MANAGER))))).isEqualTo("ADJUSTMENT_NOT_PENDING");
	}

	@Test
	void anIdempotencyKeyMakesARetrySafeAndRefusesADifferentBody() throws Exception {
		UUID company = seedCompany();
		String key = "adj-" + UUID.randomUUID();
		HttpResponse<String> first = ask(ops, company, 2_000, "Correction", key);
		HttpResponse<String> retry = ask(ops, company, 2_000, "Correction", key);
		assertThat(JsonPath.<String>read(retry.body(), "$.id")).isEqualTo(JsonPath.<String>read(first.body(), "$.id"));
		HttpResponse<String> other = ask(ops, company, 3_000, "Correction", key);
		assertThat(other.statusCode()).isEqualTo(409);
		assertThat(code(other)).isEqualTo("IDEMPOTENCY_KEY_REUSED");
		assertThat(Owner.<Long>queryOne("select count(*) from wallet_adjustments where company_id = ?", company)).isEqualTo(1L);

		// The same key raced from two connections still creates one adjustment.
		String raced = "adj-" + UUID.randomUUID();
		List<Integer> statuses = Concurrently.run(4, (i) -> ask(ops, company, 4_000, "Race", raced).statusCode());
		assertThat(statuses).allMatch((s) -> s == 201);
		assertThat(Owner.<Long>queryOne("select count(*) from wallet_adjustments where company_id = ? and reason = 'Race'",
				company)).isEqualTo(1L);
	}

	@Test
	void racingApprovalsPostOnce() throws Exception {
		UUID company = seedCompany();
		String id = JsonPath.read(ask(ops, company, 9_000, "Race approvals", null).body(), "$.id");
		List<SignedIn> approvers = List.of(manager, signIn(seed(Role.OPS_MANAGER)), signIn(seed(Role.SUPER_ADMIN)));
		List<Integer> statuses = Concurrently.run(3, (i) -> approve(id, approvers.get(i)).statusCode());
		assertThat(statuses).allMatch((s) -> s == 200);
		assertThat(LedgerTests.cachedBalance(company)).isEqualTo(9_000);
		assertThat(Owner.<Long>queryOne("select count(*) from ledger_transactions where source_id = ?", UUID.fromString(id)))
			.isEqualTo(1L);
	}

	private HttpResponse<String> ask(SignedIn who, UUID company, long amount, String reason, String key)
			throws Exception {
		HttpRequest.Builder builder = request("/api/v1/ops/companies/" + company + "/wallet/adjustments")
			.header("Authorization", "Bearer " + who.accessToken())
			.header("Content-Type", "application/json");
		if (key != null) {
			builder.header("Idempotency-Key", key);
		}
		return send(builder.POST(HttpRequest.BodyPublishers
			.ofString("{\"amountPiastres\":" + amount + ",\"reason\":\"" + reason + "\"}")));
	}

	private HttpResponse<String> approve(String id, SignedIn who) throws Exception {
		return authed("POST", "/api/v1/ops/wallet-adjustments/" + id + "/approve", who.accessToken(), null);
	}

}
