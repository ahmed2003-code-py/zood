package com.zawed.api.stationfinance;

import java.time.Instant;
import java.util.UUID;

import jakarta.validation.Valid;

import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.tags.Tag;

import org.springframework.http.CacheControl;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.RestController;

import com.zawed.api.platform.security.Permission;
import com.zawed.api.platform.security.RequiresPermission;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiContract;
import com.zawed.api.platform.web.Page;
import com.zawed.api.stationfinance.StationFinanceApi.BankAccountChangeBody;
import com.zawed.api.stationfinance.StationFinanceApi.BankAccountResponse;
import com.zawed.api.stationfinance.StationFinanceApi.CancelPayoutRequest;
import com.zawed.api.stationfinance.StationFinanceApi.HeldPayableResponse;
import com.zawed.api.stationfinance.StationFinanceApi.PayoutResponse;
import com.zawed.api.stationfinance.StationFinanceApi.RecordPayoutRequest;
import com.zawed.api.stationfinance.StationFinanceApi.RejectBankAccountChangeRequest;
import com.zawed.api.stationfinance.StationFinanceApi.RejectReversalRequest;
import com.zawed.api.stationfinance.StationFinanceApi.ReleaseHoldRequest;
import com.zawed.api.stationfinance.StationFinanceApi.RequestReversalBody;
import com.zawed.api.stationfinance.StationFinanceApi.ReversalResponse;
import com.zawed.api.stationfinance.StationFinanceApi.StationBankDetailsResponse;
import com.zawed.api.stationfinance.StationFinanceApi.StationPayableResponse;
import com.zawed.api.stationfinance.StationFinanceApi.StationStatementEntryResponse;
import com.zawed.api.stationfinance.StationFinanceApi.StationStatementSummaryResponse;

/**
 * Station finance in the Ops console (B6 contract): the station's payable and statement, its bank
 * account under change control, payouts, held payables and fueling reversals. Each "ask" and each
 * "decide" has its own permission; that a person never decides their own request is a rule of the
 * services and of the database, not of this class.
 */
@RestController
@RequestMapping(ApiContract.BASE_PATH + "/ops")
@Tag(name = "station finance")
class OpsStationFinanceController {

	private final StationStatements statements;
	private final BankAccounts bankAccounts;
	private final Payouts payouts;
	private final PayableHolds holds;
	private final Reversals reversals;

	private final StationFinanceViews views;

	OpsStationFinanceController(StationStatements statements, BankAccounts bankAccounts, Payouts payouts,
			PayableHolds holds, Reversals reversals, StationFinanceViews views) {
		this.views = views;
		this.statements = statements;
		this.bankAccounts = bankAccounts;
		this.payouts = payouts;
		this.holds = holds;
		this.reversals = reversals;
	}

	// ------------------------------------------------------------------------------- statement

	@GetMapping("/stations/{id}/payable")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "getStationPayable", summary = "What a station is owed, what is held and what may be paid out")
	StationPayableResponse payable(@AuthenticationPrincipal ZawedPrincipal principal, @PathVariable UUID id) {
		return StationPayableResponse.of(id, statements.position(principal, id), true);
	}

	@GetMapping("/stations/{id}/statement")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "getStationStatement",
			summary = "A station's payable entries, newest first, with the running balance and the fueling or payout behind each")
	Page<StationStatementEntryResponse> statement(@AuthenticationPrincipal ZawedPrincipal principal,
			@PathVariable UUID id, @RequestParam(required = false) String cursor,
			@RequestParam(required = false) Integer limit) {
		var page = statements.statement(principal, id, cursor, limit);
		return new Page<>(page.items().stream().map(StationStatementEntryResponse::of).toList(), page.nextCursor());
	}

	@GetMapping("/stations/{id}/statement/summary")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "getStationStatementSummary",
			summary = "Opening balance, credits, debits and closing balance of a period, with the station's position now")
	StationStatementSummaryResponse summary(@AuthenticationPrincipal ZawedPrincipal principal, @PathVariable UUID id,
			@RequestParam(required = false) Instant from, @RequestParam(required = false) Instant to) {
		return StationStatementSummaryResponse.of(statements.period(principal, id, from, to), true);
	}

	// --------------------------------------------------------------------------- bank accounts

	@GetMapping("/stations/{id}/bank-account")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "getStationBankAccount", summary = "A station's verified bank account and any change that waits, masked")
	ResponseEntity<StationBankDetailsResponse> bankAccount(@AuthenticationPrincipal ZawedPrincipal principal,
			@PathVariable UUID id) {
		return noStore(views.details(principal, bankAccounts.details(principal, id)));
	}

	@PostMapping("/stations/{id}/bank-account/changes")
	@RequiresPermission(Permission.BANK_DETAILS_MANAGE)
	@Operation(operationId = "requestBankAccountChange",
			summary = "Request a change of a station's bank account; a different person must approve it")
	ResponseEntity<BankAccountResponse> requestChange(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody BankAccountChangeBody body) {
		return ResponseEntity.status(HttpStatus.CREATED)
			.cacheControl(CacheControl.noStore())
			.body(views.bankAccount(actor, bankAccounts.requestChange(actor, id, body.accountNumber(),
					body.bankName(), body.holderName(), body.bankLetterReference())));
	}

	@GetMapping("/bank-account-changes")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "listBankAccountChanges", summary = "Bank account changes, newest first; filter by status, station and operator")
	ResponseEntity<Page<BankAccountResponse>> changes(@AuthenticationPrincipal ZawedPrincipal viewer,
			@RequestParam(required = false) BankAccounts.Status status,
			@RequestParam(required = false) UUID stationId, @RequestParam(required = false) UUID operatorId,
			@RequestParam(required = false) String cursor, @RequestParam(required = false) Integer limit) {
		var page = bankAccounts.changes(status, stationId, operatorId, cursor, limit);
		return noStore(views.bankAccounts(viewer, page));
	}

	@PostMapping("/bank-account-changes/{id}/approve")
	@RequiresPermission(Permission.BANK_DETAILS_APPROVE)
	@Operation(operationId = "approveBankAccountChange",
			summary = "Approve a bank account change (never your own request); starts the payout hold")
	ResponseEntity<BankAccountResponse> approveChange(@AuthenticationPrincipal ZawedPrincipal actor,
			@PathVariable UUID id) {
		return noStore(views.bankAccount(actor, bankAccounts.approve(actor, id)));
	}

	@PostMapping("/bank-account-changes/{id}/reject")
	@RequiresPermission(Permission.BANK_DETAILS_APPROVE)
	@Operation(operationId = "rejectBankAccountChange", summary = "Reject a bank account change with a reason")
	ResponseEntity<BankAccountResponse> rejectChange(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody RejectBankAccountChangeRequest body) {
		return noStore(views.bankAccount(actor, bankAccounts.reject(actor, id, body.reason().trim())));
	}

	// --------------------------------------------------------------------------------- payouts

	@GetMapping("/payouts")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "listPayouts", summary = "Payouts, newest first; filter by station, operator and status")
	Page<PayoutResponse> payouts(@AuthenticationPrincipal ZawedPrincipal viewer, @RequestParam(required = false) UUID stationId,
			@RequestParam(required = false) UUID operatorId, @RequestParam(required = false) Payouts.Status status,
			@RequestParam(required = false) String cursor, @RequestParam(required = false) Integer limit) {
		var page = payouts.list(new Payouts.Filter(operatorId, stationId, status), cursor, limit);
		return views.payouts(viewer, page);
	}

	@GetMapping("/payouts/{id}")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "getPayout", summary = "One payout")
	PayoutResponse payout(@AuthenticationPrincipal ZawedPrincipal viewer, @PathVariable UUID id) {
		return views.payout(viewer, payouts.get(id));
	}

	@PostMapping("/stations/{id}/payouts")
	@RequiresPermission(Permission.PAYOUTS_MANAGE)
	@Operation(operationId = "recordPayout",
			summary = "Record a bank transfer to a station; posts at once up to the approval threshold, otherwise waits for a second person")
	ResponseEntity<PayoutResponse> recordPayout(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody RecordPayoutRequest body) {
		return ResponseEntity.status(HttpStatus.CREATED)
			.body(views.payout(actor, payouts.record(actor, id, body.amountPiastres(), body.bankReference(),
					body.valueDate(), body.note())));
	}

	@PostMapping("/payouts/{id}/approve")
	@RequiresPermission(Permission.PAYOUTS_APPROVE)
	@Operation(operationId = "approvePayout", summary = "Approve a payout that waits (never one you recorded); posts it")
	PayoutResponse approvePayout(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id) {
		return views.payout(actor, payouts.approve(actor, id));
	}

	@PostMapping("/payouts/{id}/cancel")
	@RequiresPermission(Permission.PAYOUTS_MANAGE)
	@Operation(operationId = "cancelPayout", summary = "Withdraw a payout that still waits for approval")
	PayoutResponse cancelPayout(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody CancelPayoutRequest body) {
		return views.payout(actor, payouts.cancel(actor, id, body.reason().trim()));
	}

	// --------------------------------------------------------------------------- held payables

	@GetMapping("/payable-holds")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "listPayableHolds",
			summary = "Fuelings whose payable was held at sync, newest first; filter by station, operator and state")
	Page<HeldPayableResponse> holds(@RequestParam(required = false) UUID stationId,
			@RequestParam(required = false) UUID operatorId, @RequestParam(required = false) PayableHolds.State state,
			@RequestParam(required = false) String cursor, @RequestParam(required = false) Integer limit) {
		var page = holds.list(operatorId, stationId, state, cursor, limit);
		return new Page<>(page.items().stream().map(HeldPayableResponse::of).toList(), page.nextCursor());
	}

	@PostMapping("/fuelings/{id}/payable-hold/release")
	@RequiresPermission(Permission.PAYABLE_HOLD_RELEASE)
	@Operation(operationId = "releasePayableHold", summary = "Release a held payable with a reason; it then counts as available")
	HeldPayableResponse release(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody ReleaseHoldRequest body) {
		return HeldPayableResponse.of(holds.release(actor, id, body.reason().trim()));
	}

	// ------------------------------------------------------------------------------- reversals

	@PostMapping("/fuelings/{id}/reversals")
	@RequiresPermission(Permission.REVERSALS_REQUEST)
	@Operation(operationId = "requestFuelingReversal",
			summary = "Request the reversal of a completed fueling; a different person must approve it")
	ResponseEntity<ReversalResponse> requestReversal(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody RequestReversalBody body) {
		return ResponseEntity.status(HttpStatus.CREATED)
			.body(ReversalResponse.of(reversals.request(actor, id, body.reason().trim())));
	}

	@GetMapping("/reversals")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "listReversals", summary = "Fueling reversals, newest first; filter by status, company, station and fueling")
	Page<ReversalResponse> reversals(@RequestParam(required = false) Reversals.Status status,
			@RequestParam(required = false) UUID companyId, @RequestParam(required = false) UUID operatorId,
			@RequestParam(required = false) UUID stationId, @RequestParam(required = false) UUID fuelingId,
			@RequestParam(required = false) String cursor, @RequestParam(required = false) Integer limit) {
		var page = reversals.list(new Reversals.Filter(companyId, operatorId, stationId, fuelingId, status), cursor, limit);
		return new Page<>(page.items().stream().map(ReversalResponse::of).toList(), page.nextCursor());
	}

	@GetMapping("/reversals/{id}")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "getReversal", summary = "One fueling reversal")
	ReversalResponse reversal(@PathVariable UUID id) {
		return ReversalResponse.of(reversals.get(id));
	}

	@PostMapping("/reversals/{id}/approve")
	@RequiresPermission(Permission.REVERSALS_APPROVE)
	@Operation(operationId = "approveReversal",
			summary = "Approve a reversal (never your own request); posts the mirror of the fueling")
	ReversalResponse approveReversal(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id) {
		return ReversalResponse.of(reversals.approve(actor, id));
	}

	@PostMapping("/reversals/{id}/reject")
	@RequiresPermission(Permission.REVERSALS_APPROVE)
	@Operation(operationId = "rejectReversal", summary = "Reject a reversal request with a reason")
	ReversalResponse rejectReversal(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody RejectReversalRequest body) {
		return ReversalResponse.of(reversals.reject(actor, id, body.reason().trim()));
	}

	/** Bank details are not for a shared cache or a browser's history, even masked. */
	private static <T> ResponseEntity<T> noStore(T body) {
		return ResponseEntity.ok().cacheControl(CacheControl.noStore()).body(body);
	}

}
