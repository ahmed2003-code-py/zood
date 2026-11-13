package com.zawed.api.fleet;

import java.util.List;
import java.util.UUID;

import jakarta.validation.Valid;

import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.tags.Tag;

import org.springframework.http.HttpStatus;
import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.web.bind.annotation.DeleteMapping;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.PutMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.ResponseStatus;
import org.springframework.web.bind.annotation.RestController;

import com.zawed.api.fleet.FleetApi.CreateVehicleRequest;
import com.zawed.api.fleet.FleetApi.IssueTagRequest;
import com.zawed.api.fleet.FleetApi.LimitRequest;
import com.zawed.api.fleet.FleetApi.LimitResponse;
import com.zawed.api.fleet.FleetApi.TagResponse;
import com.zawed.api.fleet.FleetApi.TagStatusRequest;
import com.zawed.api.fleet.FleetApi.VehicleBody;
import com.zawed.api.fleet.FleetApi.VehicleDetail;
import com.zawed.api.fleet.FleetApi.VehicleResponse;
import com.zawed.api.fleet.FleetApi.VehicleStatusRequest;
import com.zawed.api.fleet.Vehicles.Status;
import com.zawed.api.fleet.Vehicles.Vehicle;
import com.zawed.api.platform.security.Permission;
import com.zawed.api.platform.security.RequiresPermission;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiContract;
import com.zawed.api.platform.web.Page;

/** Vehicles, RFID tags and limits in the Ops console (PRD §6.3). */
@RestController
@RequestMapping(ApiContract.BASE_PATH + "/ops")
@Tag(name = "fleet")
class FleetController {

	private final Vehicles vehicles;
	private final Tags tags;

	FleetController(Vehicles vehicles, Tags tags) {
		this.vehicles = vehicles;
		this.tags = tags;
	}

	@GetMapping("/vehicles")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "listVehicles", summary = "Vehicles, newest first; q matches the plate")
	Page<VehicleResponse> list(@RequestParam(required = false) UUID companyId,
			@RequestParam(required = false) UUID departmentId, @RequestParam(required = false) Status status,
			@RequestParam(required = false) String q, @RequestParam(required = false) String cursor,
			@RequestParam(required = false) Integer limit) {
		Page<Vehicle> page = vehicles.list(companyId, departmentId, status, q, cursor, limit);
		return new Page<>(page.items().stream().map(VehicleResponse::of).toList(), page.nextCursor());
	}

	@PostMapping("/vehicles")
	@ResponseStatus(HttpStatus.CREATED)
	@RequiresPermission(Permission.FLEET_MANAGE)
	@Operation(operationId = "createVehicle", summary = "Register a vehicle for a company")
	VehicleResponse create(@AuthenticationPrincipal ZawedPrincipal actor,
			@Valid @RequestBody CreateVehicleRequest body) {
		return VehicleResponse.of(vehicles.create(actor, body.companyId(), body.vehicle().draft()));
	}

	@GetMapping("/vehicles/{id}")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "getVehicle", summary = "A vehicle with its tags and active limits")
	VehicleDetail get(@PathVariable UUID id) {
		return new VehicleDetail(VehicleResponse.of(vehicles.get(id)),
				tags.ofVehicle(id).stream().map(TagResponse::of).toList(),
				vehicles.limits(id).stream().map(LimitResponse::of).toList());
	}

	@PutMapping("/vehicles/{id}")
	@RequiresPermission(Permission.FLEET_MANAGE)
	@Operation(operationId = "updateVehicle", summary = "Edit a vehicle")
	VehicleResponse update(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody VehicleBody body) {
		return VehicleResponse.of(vehicles.update(actor, id, body.draft()));
	}

	@PostMapping("/vehicles/{id}/status")
	@RequiresPermission(Permission.FLEET_MANAGE)
	@Operation(operationId = "setVehicleStatus", summary = "Suspend, reactivate or decommission a vehicle")
	VehicleResponse status(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody VehicleStatusRequest body) {
		return VehicleResponse.of(vehicles.setStatus(actor, id, body.status(), body.reason().trim()));
	}

	@PostMapping("/vehicles/{id}/tags")
	@ResponseStatus(HttpStatus.CREATED)
	@RequiresPermission(Permission.FLEET_MANAGE)
	@Operation(operationId = "issueTag", summary = "Bind a new RFID tag to the vehicle")
	TagResponse issueTag(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody IssueTagRequest body) {
		return TagResponse.of(tags.issue(actor, id, body.uid()));
	}

	@PostMapping("/tags/{id}/status")
	@RequiresPermission(Permission.FLEET_MANAGE)
	@Operation(operationId = "setTagStatus", summary = "Report a tag lost or stolen, deactivate it, or flag it for replacement")
	TagResponse tagStatus(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody TagStatusRequest body) {
		return TagResponse.of(tags.setStatus(actor, id, body.status(), body.reason().trim()));
	}

	@PutMapping("/vehicles/{id}/limits")
	@RequiresPermission(Permission.FLEET_MANAGE)
	@Operation(operationId = "setVehicleLimit", summary = "Set the limit for one kind and period, replacing the previous one")
	List<LimitResponse> setLimit(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody LimitRequest body) {
		vehicles.setLimit(actor, id, body.kind(), body.period(), body.maxValue());
		return vehicles.limits(id).stream().map(LimitResponse::of).toList();
	}

	@DeleteMapping("/vehicles/{id}/limits/{limitId}")
	@ResponseStatus(HttpStatus.NO_CONTENT)
	@RequiresPermission(Permission.FLEET_MANAGE)
	@Operation(operationId = "removeVehicleLimit", summary = "Retire a limit")
	void removeLimit(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@PathVariable UUID limitId) {
		vehicles.removeLimit(actor, id, limitId);
	}

}
