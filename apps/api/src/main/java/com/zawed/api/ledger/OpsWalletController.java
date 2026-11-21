package com.zawed.api.ledger;

import java.util.List;
import java.util.UUID;

import jakarta.validation.Valid;

import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.Parameter;
import io.swagger.v3.oas.annotations.enums.ParameterIn;
import io.swagger.v3.oas.annotations.tags.Tag;

import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestHeader;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.ResponseStatus;
import org.springframework.web.bind.annotation.RestController;

import com.zawed.api.ledger.LedgerApi.ReconciliationFindingResponse;
import com.zawed.api.ledger.LedgerApi.ReconciliationRunResponse;
import com.zawed.api.ledger.LedgerApi.RejectTopUpRequest;
import com.zawed.api.ledger.LedgerApi.StatementEntryResponse;
import com.zawed.api.ledger.LedgerApi.RecordTransferRequest;
import com.zawed.api.ledger.LedgerApi.RejectWalletAdjustmentRequest;
import com.zawed.api.ledger.LedgerApi.VerifyTopUpRequest;
import com.zawed.api.ledger.LedgerApi.WalletAdjustmentRequest;
import com.zawed.api.ledger.LedgerApi.WalletAdjustmentResponse;
import com.zawed.api.ledger.LedgerApi.TopUpResponse;
import com.zawed.api.ledger.LedgerApi.WalletResponse;
import com.zawed.api.ledger.Reconciliation.Finding;
import com.zawed.api.ledger.Reconciliation.Run;
import com.zawed.api.ledger.TopUps.Status;
import com.zawed.api.ledger.TopUps.TopUp;
import com.zawed.api.ledger.Wallets.StatementEntry;
import com.zawed.api.platform.persistence.IdempotencyKeys;
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
	private final WalletAdjustments adjustments;
	private final LedgerViews views;

	OpsWalletController(Wallets wallets, TopUps topUps, Reconciliation reconciliation, WalletAdjustments adjustments,
			LedgerViews views) {
		this.views = views;
		this.wallets = wallets;
		this.topUps = topUps;
		this.reconciliation = reconciliation;
		this.adjustments = adjustments;
	}

	// ----------------------------------------------------------------------------------- top-ups

	@GetMapping("/topups")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "listTopUps", summary = "Top-up requests, newest first; filter by status and company")
	Page<TopUpResponse> list(@AuthenticationPrincipal ZawedPrincipal viewer,
			@RequestParam(required = false) Status status, @RequestParam(required = false) UUID companyId,
			@RequestParam(required = false) String cursor, @RequestParam(required = false) Integer limit) {
		Page<TopUp> page = topUps.list(companyId, status, cursor, limit);
		return views.topUps(viewer, page);
	}

	@GetMapping("/topups/{id}")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "getTopUp", summary = "One top-up request")
	TopUpResponse get(@AuthenticationPrincipal ZawedPrincipal viewer, @PathVariable UUID id) {
		return views.topUp(viewer, topUps.get(id));
	}

	@GetMapping("/topups/receiving-accounts")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "listReceivingAccounts", summary = "ZAWED's bank accounts a transfer can arrive in")
	List<String> receivingAccounts() {
		return topUps.receivingAccounts().stream().sorted().toList();
	}

	@PostMapping("/topups/{id}/verify")
	@RequiresPermission(Permission.WALLET_CREDIT)
	@Operation(operationId = "verifyTopUp",
			summary = "Step 2: record what arrived on ZAWED's bank statement; a different person then approves")
	TopUpResponse verify(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody VerifyTopUpRequest body) {
		return views.topUp(actor, topUps.verify(actor, id, new TopUps.Verification(body.receivingAccount(),
				body.bankReference(), body.receivedAmountPiastres(), body.transferDate(), body.evidenceReference(),
				body.note())));
	}

	@PostMapping("/topups/{id}/approve")
	@RequiresPermission(Permission.TOPUP_APPROVE)
	@Operation(operationId = "approveTopUp",
			summary = "Step 3: approve a verified top-up (never one you verified); credits the verified amount once")
	TopUpResponse approve(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id) {
		return views.topUp(actor, topUps.approve(actor, id));
	}

	@PostMapping("/topups/{id}/reject")
	@RequiresPermission(Permission.WALLET_CREDIT)
	@Operation(operationId = "rejectTopUp", summary = "Reject a top-up request with a reason")
	TopUpResponse reject(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody RejectTopUpRequest body) {
		return views.topUp(actor, topUps.reject(actor, id, body.reason().trim()));
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
	@RequiresPermission(Permission.WALLET_CREDIT)
	@Operation(operationId = "recordBankTransfer",
			summary = "Record a verified bank transfer that has no company request; it waits for a second person's approval")
	ResponseEntity<TopUpResponse> credit(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody RecordTransferRequest body) {
		TopUps.Recorded recorded = topUps.recordTransfer(actor, id, new TopUps.Verification(body.receivingAccount(),
				body.bankReference(), body.amountPiastres(), body.transferDate(), body.evidenceReference(), body.note()));
		return ResponseEntity.status(recorded.replayed() ? HttpStatus.OK : HttpStatus.CREATED)
			.body(views.topUp(actor, recorded.topUp()));
	}

	// ---------------------------------------------------------------------------- adjustments

	@GetMapping("/wallet-adjustments")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "listWalletAdjustments", summary = "Wallet adjustments, newest first; filter by status and company")
	Page<WalletAdjustmentResponse> adjustments(@AuthenticationPrincipal ZawedPrincipal viewer,
			@RequestParam(required = false) WalletAdjustments.Status status,
			@RequestParam(required = false) UUID companyId, @RequestParam(required = false) String cursor,
			@RequestParam(required = false) Integer limit) {
		var page = adjustments.list(companyId, status, cursor, limit);
		return views.adjustments(viewer, page);
	}

	@PostMapping("/companies/{id}/wallet/adjustments")
	@RequiresPermission(Permission.WALLET_CREDIT)
	@Operation(operationId = "requestWalletAdjustment",
			summary = "Ask for a wallet credit that is not a bank transfer, with a reason; a different person approves")
	ResponseEntity<WalletAdjustmentResponse> requestAdjustment(@AuthenticationPrincipal ZawedPrincipal actor,
			@PathVariable UUID id,
			// Required, but bound as optional on purpose: the service refuses a missing key with its own code
			// (400 INVALID_IDEMPOTENCY_KEY, field Idempotency-Key). The permission is checked before any
			// argument is bound (platform.security.PermissionFirst).
			@Parameter(name = IdempotencyKeys.HEADER, in = ParameterIn.HEADER, required = true,
					description = "Required: 8 to 200 characters, new for each adjustment and the same on every retry of it; "
							+ "missing or blank answers 400 INVALID_IDEMPOTENCY_KEY")
			@RequestHeader(name = IdempotencyKeys.HEADER, required = false) String idempotencyKey,
			@Valid @RequestBody WalletAdjustmentRequest body) {
		return ResponseEntity.status(HttpStatus.CREATED).body(views.adjustment(actor,
				adjustments.request(actor, id, idempotencyKey, body, body.amountPiastres(), body.reason())));
	}

	@PostMapping("/wallet-adjustments/{id}/approve")
	@RequiresPermission(Permission.TOPUP_APPROVE)
	@Operation(operationId = "approveWalletAdjustment",
			summary = "Approve an adjustment (never your own request); credits the wallet once")
	WalletAdjustmentResponse approveAdjustment(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id) {
		return views.adjustment(actor, adjustments.approve(actor, id));
	}

	@PostMapping("/wallet-adjustments/{id}/reject")
	@RequiresPermission(Permission.TOPUP_APPROVE)
	@Operation(operationId = "rejectWalletAdjustment", summary = "Reject an adjustment with a reason")
	WalletAdjustmentResponse rejectAdjustment(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody RejectWalletAdjustmentRequest body) {
		return views.adjustment(actor, adjustments.reject(actor, id, body.reason().trim()));
	}

	@PostMapping("/wallet-adjustments/{id}/cancel")
	@RequiresPermission(Permission.WALLET_CREDIT)
	@Operation(operationId = "cancelWalletAdjustment", summary = "Withdraw your own adjustment request")
	WalletAdjustmentResponse cancelAdjustment(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id) {
		return views.adjustment(actor, adjustments.cancel(actor, id));
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
