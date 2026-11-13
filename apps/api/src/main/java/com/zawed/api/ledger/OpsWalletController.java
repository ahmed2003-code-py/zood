package com.zawed.api.ledger;

import java.util.UUID;

import jakarta.validation.Valid;

import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.tags.Tag;

import org.springframework.http.HttpStatus;
import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.ResponseStatus;
import org.springframework.web.bind.annotation.RestController;

import com.zawed.api.ledger.LedgerApi.ConfirmTopUpRequest;
import com.zawed.api.ledger.LedgerApi.ReconciliationFindingResponse;
import com.zawed.api.ledger.LedgerApi.ReconciliationRunResponse;
import com.zawed.api.ledger.LedgerApi.RejectTopUpRequest;
import com.zawed.api.ledger.LedgerApi.StatementEntryResponse;
import com.zawed.api.ledger.LedgerApi.TopUpRequestBody;
import com.zawed.api.ledger.LedgerApi.TopUpResponse;
import com.zawed.api.ledger.LedgerApi.WalletResponse;
import com.zawed.api.ledger.Reconciliation.Finding;
import com.zawed.api.ledger.Reconciliation.Run;
import com.zawed.api.ledger.TopUps.Status;
import com.zawed.api.ledger.TopUps.TopUp;
import com.zawed.api.ledger.Wallets.StatementEntry;
import com.zawed.api.platform.security.Permission;
import com.zawed.api.platform.security.RequiresPermission;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiContract;
import com.zawed.api.platform.web.Page;

/** Wallets, the top-up queue and reconciliation in the Ops console (PRD §6.3, §6.4). */
@RestController
@RequestMapping(ApiContract.BASE_PATH + "/ops")
@Tag(name = "wallets")
class OpsWalletController {

	private final Wallets wallets;
	private final TopUps topUps;
	private final Reconciliation reconciliation;

	OpsWalletController(Wallets wallets, TopUps topUps, Reconciliation reconciliation) {
		this.wallets = wallets;
		this.topUps = topUps;
		this.reconciliation = reconciliation;
	}

	// ----------------------------------------------------------------------------------- top-ups

	@GetMapping("/topups")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "listTopUps", summary = "Top-up requests, newest first; filter by status and company")
	Page<TopUpResponse> list(@RequestParam(required = false) Status status, @RequestParam(required = false) UUID companyId,
			@RequestParam(required = false) String cursor, @RequestParam(required = false) Integer limit) {
		Page<TopUp> page = topUps.list(companyId, status, cursor, limit);
		return new Page<>(page.items().stream().map(TopUpResponse::of).toList(), page.nextCursor());
	}

	@GetMapping("/topups/{id}")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "getTopUp", summary = "One top-up request")
	TopUpResponse get(@PathVariable UUID id) {
		return TopUpResponse.of(topUps.get(id));
	}

	@PostMapping("/topups/{id}/confirm")
	@RequiresPermission(Permission.WALLET_CREDIT)
	@Operation(operationId = "confirmTopUp", summary = "Confirm the transfer and credit the wallet")
	TopUpResponse confirm(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody ConfirmTopUpRequest body) {
		return TopUpResponse.of(topUps.confirm(actor, id, body.bankReference(), body.note()));
	}

	@PostMapping("/topups/{id}/reject")
	@RequiresPermission(Permission.WALLET_CREDIT)
	@Operation(operationId = "rejectTopUp", summary = "Reject a top-up request with a reason")
	TopUpResponse reject(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody RejectTopUpRequest body) {
		return TopUpResponse.of(topUps.reject(actor, id, body.reason().trim()));
	}

	// ----------------------------------------------------------------------------------- wallets

	@GetMapping("/companies/{id}/wallet")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "getCompanyWallet", summary = "A company's wallet balance and low-balance state")
	WalletResponse wallet(@PathVariable UUID id) {
		return WalletResponse.of(wallets.get(id));
	}

	@GetMapping("/companies/{id}/wallet/statement")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "getCompanyWalletStatement", summary = "A company's wallet entries, newest first")
	Page<StatementEntryResponse> statement(@PathVariable UUID id, @RequestParam(required = false) String cursor,
			@RequestParam(required = false) Integer limit) {
		Page<StatementEntry> page = wallets.statement(id, cursor, limit);
		return new Page<>(page.items().stream().map(StatementEntryResponse::of).toList(), page.nextCursor());
	}

	@PostMapping("/companies/{id}/wallet/credits")
	@ResponseStatus(HttpStatus.CREATED)
	@RequiresPermission(Permission.WALLET_CREDIT)
	@Operation(operationId = "creditCompanyWallet", summary = "Credit a bank transfer that has no request behind it")
	TopUpResponse credit(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody TopUpRequestBody body) {
		return TopUpResponse.of(topUps.creditDirectly(actor, id, body.amountPiastres(), body.bankReference(),
				body.transferDate(), body.note()));
	}

	// ---------------------------------------------------------------------------- reconciliation

	@GetMapping("/reconciliation/runs")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "listReconciliationRuns", summary = "Reconciliation runs, newest first")
	Page<ReconciliationRunResponse> runs(@RequestParam(required = false) String cursor,
			@RequestParam(required = false) Integer limit) {
		Page<Run> page = reconciliation.runs(cursor, limit);
		return new Page<>(page.items().stream().map(ReconciliationRunResponse::of).toList(), page.nextCursor());
	}

	@PostMapping("/reconciliation/runs")
	@ResponseStatus(HttpStatus.CREATED)
	@RequiresPermission(Permission.RECONCILIATION_RUN)
	@Operation(operationId = "runReconciliation", summary = "Compare every cached wallet balance with its ledger now (Super Admin)")
	ReconciliationRunResponse runNow(@AuthenticationPrincipal ZawedPrincipal actor) {
		return ReconciliationRunResponse.of(reconciliation.runNow(actor));
	}

	@GetMapping("/reconciliation/runs/{id}/findings")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "listReconciliationFindings", summary = "A run's findings")
	Page<ReconciliationFindingResponse> findings(@PathVariable UUID id, @RequestParam(required = false) String cursor,
			@RequestParam(required = false) Integer limit) {
		Page<Finding> page = reconciliation.findings(id, cursor, limit);
		return new Page<>(page.items().stream().map(ReconciliationFindingResponse::of).toList(), page.nextCursor());
	}

}
