package com.zawed.api.fueling;

import java.time.Instant;
import java.util.UUID;

import jakarta.servlet.http.HttpServletRequest;
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

import com.zawed.api.fueling.FuelingApi.AlertResponse;
import com.zawed.api.fueling.FuelingApi.FuelingResponse;
import com.zawed.api.fueling.FuelingApi.PosFuelingRequest;
import com.zawed.api.fueling.FuelingApi.PosFuelingResponse;
import com.zawed.api.fueling.FuelingApi.ReceiptResponse;
import com.zawed.api.fueling.FuelingEngine.Result;
import com.zawed.api.fueling.Fuelings.Filter;
import com.zawed.api.fueling.Fuelings.Fueling;
import com.zawed.api.fueling.Fuelings.Status;
import com.zawed.api.network.PosPrincipal;
import com.zawed.api.platform.security.Permission;
import com.zawed.api.platform.security.RequiresPermission;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiContract;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.platform.web.ClientInfo;
import com.zawed.api.platform.web.Page;

/**
 * Fuelings: the POS records one (plan §3.4); Operations, the company and the station read them;
 * Operations acknowledges hard alerts. Portal reads filter by the caller's own scope, and row-level
 * security hides every other tenant's rows anyway.
 */
@RestController
@RequestMapping(ApiContract.BASE_PATH)
class FuelingsController {

	private final FuelingEngine engine;
	private final Fuelings fuelings;
	private final Alerts alerts;
	private final Receipts receipts;

	FuelingsController(FuelingEngine engine, Fuelings fuelings, Alerts alerts, Receipts receipts) {
		this.engine = engine;
		this.fuelings = fuelings;
		this.alerts = alerts;
		this.receipts = receipts;
	}

	// ----------------------------------------------------------------------------------------- POS

	@PostMapping("/pos/fuelings")
	@Tag(name = "pos")
	@Operation(operationId = "recordPosFueling",
			summary = "Decide and record a fueling; 201 when new, 200 with replayed=true for a clientTxnId seen before")
	ResponseEntity<PosFuelingResponse> record(@AuthenticationPrincipal PosPrincipal pos,
			@Valid @RequestBody PosFuelingRequest body) {
		Result result = engine.fuel(pos, body.toRequest());
		return ResponseEntity.status(result.replayed() ? HttpStatus.OK : HttpStatus.CREATED)
			.cacheControl(CacheControl.noStore())
			.body(PosFuelingResponse.of(result, receipts.pathOf(result.fueling())));
	}

	// ------------------------------------------------------------------------------------- receipt

	@GetMapping("/receipts/{id}")
	@Tag(name = "receipts")
	@Operation(operationId = "getReceipt", summary = "A fueling receipt behind its signed link")
	ResponseEntity<ReceiptResponse> receipt(@PathVariable UUID id, @RequestParam(required = false) String sig,
			HttpServletRequest request) {
		Fueling fueling = receipts.open(id, sig, ClientInfo.address(request));
		return ResponseEntity.ok().cacheControl(CacheControl.noStore()).body(ReceiptResponse.of(fueling));
	}

	// ----------------------------------------------------------------------------------------- Ops

	@GetMapping("/ops/fuelings")
	@Tag(name = "fuelings")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "listFuelings", summary = "Every fueling, newest first")
	Page<FuelingResponse> opsList(@RequestParam(required = false) UUID companyId,
			@RequestParam(required = false) UUID operatorId, @RequestParam(required = false) UUID stationId,
			@RequestParam(required = false) UUID vehicleId, @RequestParam(required = false) Status status,
			@RequestParam(required = false) Instant from, @RequestParam(required = false) Instant to,
			@RequestParam(required = false) String cursor, @RequestParam(required = false) Integer limit) {
		return page(fuelings.list(new Filter(companyId, operatorId, stationId, vehicleId, status, from, to), cursor,
				limit));
	}

	@GetMapping("/ops/fuelings/{id}")
	@Tag(name = "fuelings")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "getFueling", summary = "One fueling")
	FuelingResponse opsGet(@PathVariable UUID id) {
		return FuelingResponse.of(fuelings.get(id));
	}

	@GetMapping("/ops/alerts")
	@Tag(name = "alerts")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "listAlerts", summary = "Hard alerts, newest first")
	Page<AlertResponse> opsAlerts(@RequestParam(required = false) UUID companyId,
			@RequestParam(required = false) Alerts.Status status, @RequestParam(required = false) Alerts.Type type,
			@RequestParam(required = false) String cursor, @RequestParam(required = false) Integer limit) {
		var page = alerts.list(companyId, status, type, cursor, limit);
		return new Page<>(page.items().stream().map(AlertResponse::of).toList(), page.nextCursor());
	}

	@PostMapping("/ops/alerts/{id}/acknowledge")
	@Tag(name = "alerts")
	@RequiresPermission(Permission.ALERTS_MANAGE)
	@Operation(operationId = "acknowledgeAlert", summary = "Mark an alert as seen and handled")
	AlertResponse acknowledge(@AuthenticationPrincipal ZawedPrincipal principal, @PathVariable UUID id) {
		return AlertResponse.of(alerts.acknowledge(principal, id));
	}

	// ------------------------------------------------------------------------------------- company

	@GetMapping("/company/fuelings")
	@Tag(name = "company portal")
	@RequiresPermission(Permission.COMPANY_READ)
	@Operation(operationId = "listMyFuelings", summary = "The company's fuelings, newest first")
	Page<FuelingResponse> companyList(@AuthenticationPrincipal ZawedPrincipal principal,
			@RequestParam(required = false) UUID vehicleId, @RequestParam(required = false) Status status,
			@RequestParam(required = false) Instant from, @RequestParam(required = false) Instant to,
			@RequestParam(required = false) String cursor, @RequestParam(required = false) Integer limit) {
		return page(fuelings.list(new Filter(principal.scope().id(), null, null, vehicleId, status, from, to), cursor,
				limit));
	}

	@GetMapping("/company/fuelings/{id}")
	@Tag(name = "company portal")
	@RequiresPermission(Permission.COMPANY_READ)
	@Operation(operationId = "getMyFueling", summary = "One of the company's fuelings")
	FuelingResponse companyGet(@AuthenticationPrincipal ZawedPrincipal principal, @PathVariable UUID id) {
		Fueling fueling = fuelings.get(id);
		if (!fueling.companyId().equals(principal.scope().id())) {
			throw ApiException.notFound("Fueling");
		}
		return FuelingResponse.of(fueling);
	}

	@GetMapping("/company/alerts")
	@Tag(name = "company portal")
	@RequiresPermission(Permission.COMPANY_READ)
	@Operation(operationId = "listMyAlerts", summary = "Alerts about the company's fleet and wallet, newest first")
	Page<AlertResponse> companyAlerts(@AuthenticationPrincipal ZawedPrincipal principal,
			@RequestParam(required = false) Alerts.Status status, @RequestParam(required = false) String cursor,
			@RequestParam(required = false) Integer limit) {
		var page = alerts.list(principal.scope().id(), status, null, cursor, limit);
		return new Page<>(page.items().stream().map(AlertResponse::of).toList(), page.nextCursor());
	}

	// ------------------------------------------------------------------------------------- station

	@GetMapping("/station/fuelings")
	@Tag(name = "station portal")
	@RequiresPermission(Permission.STATION_READ)
	@Operation(operationId = "listStationFuelings", summary = "Fuelings at the operator's stations, newest first")
	Page<FuelingResponse> stationList(@AuthenticationPrincipal ZawedPrincipal principal,
			@RequestParam(required = false) UUID stationId, @RequestParam(required = false) Status status,
			@RequestParam(required = false) Instant from, @RequestParam(required = false) Instant to,
			@RequestParam(required = false) String cursor, @RequestParam(required = false) Integer limit) {
		return page(fuelings.list(new Filter(null, principal.scope().id(), stationId, null, status, from, to), cursor,
				limit));
	}

	@GetMapping("/station/fuelings/{id}")
	@Tag(name = "station portal")
	@RequiresPermission(Permission.STATION_READ)
	@Operation(operationId = "getStationFueling", summary = "One fueling at the operator's stations")
	FuelingResponse stationGet(@AuthenticationPrincipal ZawedPrincipal principal, @PathVariable UUID id) {
		Fueling fueling = fuelings.get(id);
		if (!fueling.operatorId().equals(principal.scope().id())) {
			throw ApiException.notFound("Fueling");
		}
		return FuelingResponse.of(fueling);
	}

	private static Page<FuelingResponse> page(Page<Fueling> page) {
		return new Page<>(page.items().stream().map(FuelingResponse::of).toList(), page.nextCursor());
	}

}
