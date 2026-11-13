package com.zawed.api.ledger;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.sql.Connection;
import java.sql.Statement;
import java.time.LocalDate;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.UUID;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;

import com.zawed.api.identity.IdentityTestSupport;
import com.zawed.api.ledger.Reconciliation.Run;
import com.zawed.api.ledger.Reconciliation.Trigger;
import com.zawed.api.platform.security.Role;
import com.zawed.api.support.Owner;

/** The nightly comparison of cached balances with the ledger (PRD §4.2), run on demand and by the job. */
class ReconciliationTests extends IdentityTestSupport {

	@Autowired
	Reconciliation reconciliation;

	@Test
	void aCorruptedCachedBalanceIsFoundAndACleanWalletIsNot() throws Exception {
		SignedIn admin = signIn(seed(Role.SUPER_ADMIN));
		SignedIn ops = signIn(seed(Role.OPS));
		UUID company = seedCompany();
		assertThat(authed("POST", "/api/v1/ops/companies/" + company + "/wallet/credits", ops.accessToken(),
				"{\"amountPiastres\":80000,\"bankReference\":\"RC-" + UUID.randomUUID() + "\",\"transferDate\":\""
						+ LocalDate.now() + "\"}").statusCode()).isEqualTo(201);

		// Clean: no wallet anywhere disagrees with its ledger.
		HttpResponse<String> clean = authed("POST", "/api/v1/ops/reconciliation/runs", admin.accessToken(), null);
		assertThat(clean.statusCode()).as(clean.body()).isEqualTo(201);
		String cleanRun = JsonPath.read(clean.body(), "$.id");
		assertThat(JsonPath.<Integer>read(clean.body(), "$.walletsChecked")).isGreaterThan(0);
		assertThat(mismatches(cleanRun)).isEmpty();

		// The owner corrupts the cache by 1,234 piastres (nothing else can: the ledger is append only).
		Owner.update("update company_wallets set balance_piastres = balance_piastres + 1234 where company_id = ?", company);
		HttpResponse<String> dirty = authed("POST", "/api/v1/ops/reconciliation/runs", admin.accessToken(), null);
		assertThat(dirty.statusCode()).isEqualTo(201);
		String dirtyRun = JsonPath.read(dirty.body(), "$.id");
		List<Map<String, Object>> found = mismatches(dirtyRun);
		assertThat(found).hasSize(1);
		assertThat(found.getFirst().get("companyId")).isEqualTo(company.toString());
		assertThat(found.getFirst().get("cachedBalancePiastres")).isEqualTo(81_234);
		assertThat(found.getFirst().get("ledgerBalancePiastres")).isEqualTo(80_000);
		assertThat(found.getFirst().get("differencePiastres")).isEqualTo(1_234);

		// Ops reads the runs, newest first, and the findings; the run is audited with its actor.
		HttpResponse<String> runs = authed("GET", "/api/v1/ops/reconciliation/runs?limit=5", ops.accessToken(), null);
		assertThat(runs.statusCode()).isEqualTo(200);
		assertThat(JsonPath.<List<String>>read(runs.body(), "$.items[*].id")).startsWith(dirtyRun, cleanRun);
		assertThat(JsonPath.<String>read(runs.body(), "$.items[0].trigger")).isEqualTo("MANUAL");
		assertThat(JsonPath.<String>read(runs.body(), "$.items[0].triggeredBy")).isEqualTo(admin.userId().toString());
		assertThat(JsonPath.<Integer>read(runs.body(), "$.items[0].findingsCount")).isGreaterThanOrEqualTo(1);
		assertThat(Owner.<UUID>queryOne(
				"select actor_id from audit_log where action = 'RECONCILIATION_RUN' and entity_id = ?", UUID.fromString(dirtyRun)))
			.isEqualTo(admin.userId());

		// Repaired: the wallet no longer appears.
		Owner.update("update company_wallets set balance_piastres = balance_piastres - 1234 where company_id = ?", company);
		String repairedRun = JsonPath.read(authed("POST", "/api/v1/ops/reconciliation/runs", admin.accessToken(), null).body(),
				"$.id");
		assertThat(mismatches(repairedRun)).isEmpty();
	}

	@Test
	void onlyOneRunAtATime() throws Exception {
		SignedIn admin = signIn(seed(Role.SUPER_ADMIN));
		// Another instance holding the job's lock, for as long as this connection is open.
		try (Connection other = Owner.connect(); Statement sql = other.createStatement()) {
			sql.execute("select pg_advisory_lock(hashtext('ledger_reconcile'))");
			HttpResponse<String> busy = authed("POST", "/api/v1/ops/reconciliation/runs", admin.accessToken(), null);
			assertThat(busy.statusCode()).isEqualTo(409);
			assertThat(code(busy)).isEqualTo("RECONCILIATION_RUNNING");
			sql.execute("select pg_advisory_unlock(hashtext('ledger_reconcile'))");
		}
		assertThat(authed("POST", "/api/v1/ops/reconciliation/runs", admin.accessToken(), null).statusCode()).isEqualTo(201);
	}

	@Test
	void theScheduledJobRunsWithNoPrincipalAndIsAudited() {
		// As the scheduler calls it: no request, no security context, no scope.
		long before = Owner.<Long>queryOne("select count(*) from reconciliation_runs where trigger = 'SCHEDULED'");
		reconciliation.nightly();
		UUID latest = Owner.queryOne(
				"select id from reconciliation_runs where trigger = 'SCHEDULED' order by started_at desc, id desc limit 1");
		assertThat(Owner.<Long>queryOne("select count(*) from reconciliation_runs where trigger = 'SCHEDULED'"))
			.isEqualTo(before + 1);
		assertThat(Owner.<Object>queryOne("select triggered_by from reconciliation_runs where id = ?", latest)).isNull();
		assertThat(Owner.<Object>queryOne("select finished_at from reconciliation_runs where id = ?", latest)).isNotNull();
		assertThat(Owner.<Long>queryOne(
				"select count(*) from audit_log where action = 'RECONCILIATION_RUN' and entity_id = ? and actor_id is null",
				latest)).isEqualTo(1L);

		// The same entry point through the proxy, as the endpoint uses it.
		Optional<Run> run = reconciliation.run(Trigger.SCHEDULED, null);
		assertThat(run).isPresent();
		assertThat(run.get().trigger()).isEqualTo(Trigger.SCHEDULED);
		assertThat(run.get().triggeredBy()).isNull();
	}

	private List<Map<String, Object>> mismatches(String runId) throws Exception {
		SignedIn ops = signIn(seed(Role.OPS));
		HttpResponse<String> findings = authed("GET", "/api/v1/ops/reconciliation/runs/" + runId + "/findings?limit=100",
				ops.accessToken(), null);
		assertThat(findings.statusCode()).as(findings.body()).isEqualTo(200);
		return JsonPath.read(findings.body(), "$.items[?(@.kind == 'BALANCE_MISMATCH')]");
	}

}
