package com.zawed.api.fueling;

import java.time.LocalDate;
import java.util.List;
import java.util.UUID;

import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.tags.Tag;

import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.RestController;

import com.zawed.api.fueling.Activity.ActivityDay;
import com.zawed.api.fueling.Activity.Subject;
import com.zawed.api.platform.security.Permission;
import com.zawed.api.platform.security.RequiresPermission;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiContract;

/** The activity calendar for each audience (plan §7 B6). Days are Cairo days, inclusive. */
@RestController
@RequestMapping(ApiContract.BASE_PATH)
class ActivityController {

	private final Activity activity;

	ActivityController(Activity activity) {
		this.activity = activity;
	}

	@GetMapping("/ops/activity")
	@Tag(name = "fuelings")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "getActivity",
			summary = "What happened each day: the platform, or one company, operator, station, vehicle, device or agent")
	List<ActivityDay> ops(@RequestParam(required = false) LocalDate from, @RequestParam(required = false) LocalDate to,
			@RequestParam(required = false) UUID companyId, @RequestParam(required = false) UUID operatorId,
			@RequestParam(required = false) UUID stationId, @RequestParam(required = false) UUID vehicleId,
			@RequestParam(required = false) UUID deviceId, @RequestParam(required = false) UUID agentId) {
		return activity.days(from, to, new Subject(companyId, operatorId, stationId, vehicleId, deviceId, agentId));
	}

	@GetMapping("/company/activity")
	@Tag(name = "company portal")
	@RequiresPermission(Permission.COMPANY_READ)
	@Operation(operationId = "getMyActivity", summary = "What happened each day to the company, or one of its vehicles")
	List<ActivityDay> company(@AuthenticationPrincipal ZawedPrincipal principal, @RequestParam(required = false) LocalDate from,
			@RequestParam(required = false) LocalDate to, @RequestParam(required = false) UUID vehicleId) {
		return activity.days(from, to, new Subject(principal.scope().id(), null, null, vehicleId, null, null));
	}

	@GetMapping("/station/activity")
	@Tag(name = "station portal")
	@RequiresPermission(Permission.STATION_READ)
	@Operation(operationId = "getStationActivity",
			summary = "What happened each day at the operator's stations, or one station, device or agent")
	List<ActivityDay> station(@AuthenticationPrincipal ZawedPrincipal principal,
			@RequestParam(required = false) LocalDate from, @RequestParam(required = false) LocalDate to,
			@RequestParam(required = false) UUID stationId, @RequestParam(required = false) UUID deviceId,
			@RequestParam(required = false) UUID agentId) {
		return activity.days(from, to, new Subject(null, principal.scope().id(), stationId, null, deviceId, agentId));
	}

}
