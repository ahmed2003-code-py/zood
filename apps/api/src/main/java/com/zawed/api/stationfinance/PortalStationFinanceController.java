package com.zawed.api.stationfinance;

import java.time.Instant;
import java.util.UUID;

import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.tags.Tag;

import org.springframework.http.CacheControl;
import org.springframework.http.ResponseEntity;
import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.RestController;

import com.zawed.api.platform.security.Permission;
import com.zawed.api.platform.security.RequiresPermission;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiContract;
import com.zawed.api.platform.web.Page;
import com.zawed.api.stationfinance.StationFinanceApi.PayoutResponse;
import com.zawed.api.stationfinance.StationFinanceApi.ReversalResponse;
import com.zawed.api.stationfinance.StationFinanceApi.StationBankDetailsResponse;
import com.zawed.api.stationfinance.StationFinanceApi.StationPayableResponse;
import com.zawed.api.stationfinance.StationFinanceApi.StationStatementEntryResponse;
import com.zawed.api.stationfinance.StationFinanceApi.StationStatementSummaryResponse;

/**
 * Station finance as the station manager and the company read it (B6 contract §6). Read only:
 * every change happens at Operations. The scope comes from the signed-in user, never from the
 * request ({@link StationAccess}), and row-level security limits what each side sees: posted
 * payouts and approved reversals only, and never which staff member asked or decided.
 */
@RestController
@RequestMapping(ApiContract.BASE_PATH)
class PortalStationFinanceController {

	private final StationStatements statements;
	private final BankAccounts bankAccounts;
	private final Payouts payouts;
	private final Reversals reversals;

	private final StationFinanceViews views;

	PortalStationFinanceController(StationStatements statements, BankAccounts bankAccounts, Payouts payouts,
			Reversals reversals, StationFinanceViews views) {
		this.views = views;
		this.statements = statements;
		this.bankAccounts = bankAccounts;
		this.payouts = payouts;
		this.reversals = reversals;
	}

	// ------------------------------------------------------------------------------------- station

	@GetMapping("/station/stations/{id}/payable")
	@Tag(name = "station portal")
	@RequiresPermission(Permission.STATION_READ)
	@Operation(operationId = "getMyStationPayable", summary = "What one of the operator's stations is owed, what is held and what is available")
	StationPayableResponse payable(@AuthenticationPrincipal ZawedPrincipal principal, @PathVariable UUID id) {
		return StationPayableResponse.of(id, statements.position(principal, id), false);
	}

	@GetMapping("/station/stations/{id}/statement")
	@Tag(name = "station portal")
	@RequiresPermission(Permission.STATION_READ)
	@Operation(operationId = "getMyStationStatement",
			summary = "The station's payable entries, newest first, with the running balance and the fueling or payout behind each")
	Page<StationStatementEntryResponse> statement(@AuthenticationPrincipal ZawedPrincipal principal,
			@PathVariable UUID id, @RequestParam(required = false) String cursor,
			@RequestParam(required = false) Integer limit) {
		var page = statements.statement(principal, id, cursor, limit);
		return new Page<>(page.items().stream().map(StationStatementEntryResponse::of).toList(), page.nextCursor());
	}

	@GetMapping("/station/stations/{id}/statement/summary")
	@Tag(name = "station portal")
	@RequiresPermission(Permission.STATION_READ)
	@Operation(operationId = "getMyStationStatementSummary",
			summary = "Opening balance, credits, debits and closing balance of a period")
	StationStatementSummaryResponse summary(@AuthenticationPrincipal ZawedPrincipal principal, @PathVariable UUID id,
			@RequestParam(required = false) Instant from, @RequestParam(required = false) Instant to) {
		return StationStatementSummaryResponse.of(statements.period(principal, id, from, to), false);
	}

	@GetMapping("/station/stations/{id}/bank-account")
	@Tag(name = "station portal")
	@RequiresPermission(Permission.STATION_READ)
	@Operation(operationId = "getMyStationBankAccount",
			summary = "Where the station is paid, masked, and any change that waits for approval")
	ResponseEntity<StationBankDetailsResponse> bankAccount(@AuthenticationPrincipal ZawedPrincipal principal,
			@PathVariable UUID id) {
		return ResponseEntity.ok()
			.cacheControl(CacheControl.noStore())
			.body(views.details(principal, bankAccounts.details(principal, id)));
	}

	@GetMapping("/station/payouts")
	@Tag(name = "station portal")
	@RequiresPermission(Permission.STATION_READ)
	@Operation(operationId = "listMyPayouts", summary = "Payouts the operator's stations received, newest first")
	Page<PayoutResponse> payouts(@AuthenticationPrincipal ZawedPrincipal principal,
			@RequestParam(required = false) UUID stationId, @RequestParam(required = false) String cursor,
			@RequestParam(required = false) Integer limit) {
		var filter = new Payouts.Filter(StationAccess.operatorFilter(principal, null), stationId, Payouts.Status.POSTED);
		var page = payouts.list(filter, cursor, limit);
		return views.payouts(principal, page);
	}

	@GetMapping("/station/reversals")
	@Tag(name = "station portal")
	@RequiresPermission(Permission.STATION_READ)
	@Operation(operationId = "listStationReversals", summary = "Approved reversals of fuelings at the operator's stations")
	Page<ReversalResponse> stationReversals(@AuthenticationPrincipal ZawedPrincipal principal,
			@RequestParam(required = false) UUID stationId, @RequestParam(required = false) String cursor,
			@RequestParam(required = false) Integer limit) {
		var filter = new Reversals.Filter(null, StationAccess.operatorFilter(principal, null), stationId, null,
				Reversals.Status.APPROVED);
		var page = reversals.list(filter, cursor, limit);
		return new Page<>(page.items().stream().map(ReversalResponse::forPortal).toList(), page.nextCursor());
	}

	// ------------------------------------------------------------------------------------- company

	@GetMapping("/company/reversals")
	@Tag(name = "company portal")
	@RequiresPermission(Permission.COMPANY_READ)
	@Operation(operationId = "listMyReversals", summary = "Approved reversals of the company's fuelings, refunded to its wallet")
	Page<ReversalResponse> companyReversals(@AuthenticationPrincipal ZawedPrincipal principal,
			@RequestParam(required = false) String cursor, @RequestParam(required = false) Integer limit) {
		var filter = new Reversals.Filter(principal.scope().id(), null, null, null, Reversals.Status.APPROVED);
		var page = reversals.list(filter, cursor, limit);
		return new Page<>(page.items().stream().map(ReversalResponse::forPortal).toList(), page.nextCursor());
	}

}
