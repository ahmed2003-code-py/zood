package com.zawed.api.network;

import java.util.UUID;

import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.tags.Tag;

import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.RestController;

import com.zawed.api.network.Shifts.Shift;
import com.zawed.api.platform.security.Permission;
import com.zawed.api.platform.security.RequiresPermission;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiContract;
import com.zawed.api.platform.web.Page;

/** Shifts (agents' sessions on devices), for "last shift" filters and the station's view (plan §7 B6). */
@RestController
@RequestMapping(ApiContract.BASE_PATH)
class ShiftsController {

	private final Shifts shifts;

	ShiftsController(Shifts shifts) {
		this.shifts = shifts;
	}

	@GetMapping("/ops/shifts")
	@Tag(name = "network")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "listShifts", summary = "Agents' shifts on devices, newest first")
	Page<Shift> ops(@RequestParam(required = false) UUID operatorId, @RequestParam(required = false) UUID stationId,
			@RequestParam(required = false) UUID deviceId, @RequestParam(required = false) UUID agentId,
			@RequestParam(required = false) String cursor, @RequestParam(required = false) Integer limit) {
		return shifts.list(operatorId, stationId, deviceId, agentId, cursor, limit);
	}

	@GetMapping("/station/shifts")
	@Tag(name = "station portal")
	@RequiresPermission(Permission.STATION_READ)
	@Operation(operationId = "listStationShifts", summary = "Shifts at the operator's stations, newest first")
	Page<Shift> station(@AuthenticationPrincipal ZawedPrincipal principal, @RequestParam(required = false) UUID stationId,
			@RequestParam(required = false) UUID deviceId, @RequestParam(required = false) UUID agentId,
			@RequestParam(required = false) String cursor, @RequestParam(required = false) Integer limit) {
		return shifts.list(principal.scope().id(), stationId, deviceId, agentId, cursor, limit);
	}

}
