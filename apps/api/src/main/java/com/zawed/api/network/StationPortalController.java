package com.zawed.api.network;

import java.util.UUID;

import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.tags.Tag;

import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.RestController;

import com.zawed.api.network.Agents.AgentStatus;
import com.zawed.api.network.Devices.DeviceStatus;
import com.zawed.api.network.NetworkApi.AgentResponse;
import com.zawed.api.network.NetworkApi.DeviceResponse;
import com.zawed.api.network.NetworkApi.OperatorResponse;
import com.zawed.api.network.NetworkApi.StationResponse;
import com.zawed.api.network.Operators.StationStatus;
import com.zawed.api.platform.security.Permission;
import com.zawed.api.platform.security.RequiresPermission;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiContract;
import com.zawed.api.platform.web.Page;

/**
 * The station portal's view of its own operator, read-only. The operator comes from the signed-in
 * user's scope, and row-level security hides every other operator's rows.
 */
@RestController
@RequestMapping(ApiContract.BASE_PATH + "/station")
@Tag(name = "station portal")
class StationPortalController {

	private final Operators operators;
	private final Devices devices;
	private final Agents agents;

	StationPortalController(Operators operators, Devices devices, Agents agents) {
		this.operators = operators;
		this.devices = devices;
		this.agents = agents;
	}

	@GetMapping("/operator")
	@RequiresPermission(Permission.STATION_READ)
	@Operation(operationId = "getMyOperator", summary = "The signed-in user's station operator")
	OperatorResponse operator(@AuthenticationPrincipal ZawedPrincipal principal) {
		return OperatorResponse.of(operators.get(principal.scope().id()));
	}

	@GetMapping("/stations")
	@RequiresPermission(Permission.STATION_READ)
	@Operation(operationId = "listMyStations", summary = "The operator's stations")
	Page<StationResponse> stations(@AuthenticationPrincipal ZawedPrincipal principal,
			@RequestParam(required = false) StationStatus status, @RequestParam(required = false) String q,
			@RequestParam(required = false) String cursor, @RequestParam(required = false) Integer limit) {
		var page = operators.stations(principal.scope().id(), status, q, cursor, limit);
		return new Page<>(page.items().stream().map(StationResponse::of).toList(), page.nextCursor());
	}

	@GetMapping("/stations/{id}")
	@RequiresPermission(Permission.STATION_READ)
	@Operation(operationId = "getMyStation", summary = "One of the operator's stations")
	StationResponse station(@PathVariable UUID id) {
		return StationResponse.of(operators.station(id));
	}

	@GetMapping("/devices")
	@RequiresPermission(Permission.STATION_READ)
	@Operation(operationId = "listMyDevices", summary = "The operator's POS devices")
	Page<DeviceResponse> devices(@AuthenticationPrincipal ZawedPrincipal principal,
			@RequestParam(required = false) UUID stationId, @RequestParam(required = false) DeviceStatus status,
			@RequestParam(required = false) String cursor, @RequestParam(required = false) Integer limit) {
		var page = devices.list(principal.scope().id(), stationId, status, cursor, limit);
		return new Page<>(page.items().stream().map(DeviceResponse::of).toList(), page.nextCursor());
	}

	@GetMapping("/agents")
	@RequiresPermission(Permission.STATION_READ)
	@Operation(operationId = "listMyAgents", summary = "The operator's agents")
	Page<AgentResponse> agents(@AuthenticationPrincipal ZawedPrincipal principal,
			@RequestParam(required = false) UUID stationId, @RequestParam(required = false) AgentStatus status,
			@RequestParam(required = false) String q, @RequestParam(required = false) String cursor,
			@RequestParam(required = false) Integer limit) {
		var page = agents.list(principal.scope().id(), stationId, status, q, cursor, limit);
		return new Page<>(page.items().stream().map(AgentResponse::of).toList(), page.nextCursor());
	}

}
