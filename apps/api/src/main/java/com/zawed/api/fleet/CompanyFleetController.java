package com.zawed.api.fleet;

import java.util.UUID;

import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.tags.Tag;

import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.RestController;

import com.zawed.api.fleet.FleetApi.LimitResponse;
import com.zawed.api.fleet.FleetApi.TagResponse;
import com.zawed.api.fleet.FleetApi.VehicleDetail;
import com.zawed.api.fleet.FleetApi.VehicleResponse;
import com.zawed.api.fleet.Vehicles.Status;
import com.zawed.api.fleet.Vehicles.Vehicle;
import com.zawed.api.platform.security.Permission;
import com.zawed.api.platform.security.RequiresPermission;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiContract;
import com.zawed.api.platform.web.Page;

/**
 * The company portal's fleet, read-only (approved decision: changes go through requests to
 * Operations). The company comes from the signed-in user's scope; row-level security hides every
 * other company's vehicles even if an id is guessed.
 */
@RestController
@RequestMapping(ApiContract.BASE_PATH + "/company/vehicles")
@Tag(name = "company portal")
class CompanyFleetController {

	private final Vehicles vehicles;
	private final Tags tags;

	CompanyFleetController(Vehicles vehicles, Tags tags) {
		this.vehicles = vehicles;
		this.tags = tags;
	}

	@GetMapping
	@RequiresPermission(Permission.COMPANY_READ)
	@Operation(operationId = "listMyVehicles", summary = "The company's vehicles; q matches the plate")
	Page<VehicleResponse> list(@AuthenticationPrincipal ZawedPrincipal principal,
			@RequestParam(required = false) UUID departmentId, @RequestParam(required = false) Status status,
			@RequestParam(required = false) String q, @RequestParam(required = false) String cursor,
			@RequestParam(required = false) Integer limit) {
		Page<Vehicle> page = vehicles.list(principal.scope().id(), departmentId, status, q, cursor, limit);
		return new Page<>(page.items().stream().map(VehicleResponse::forCompany).toList(), page.nextCursor());
	}

	@GetMapping("/{id}")
	@RequiresPermission(Permission.COMPANY_READ)
	@Operation(operationId = "getMyVehicle", summary = "One of the company's vehicles with its tags and limits")
	VehicleDetail get(@PathVariable UUID id) {
		return new VehicleDetail(VehicleResponse.forCompany(vehicles.get(id)),
				tags.ofVehicle(id).stream().map(TagResponse::of).toList(),
				vehicles.limits(id).stream().map(LimitResponse::of).toList());
	}

}
