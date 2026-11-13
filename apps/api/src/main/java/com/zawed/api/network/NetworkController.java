package com.zawed.api.network;

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
import org.springframework.web.bind.annotation.PutMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.ResponseStatus;
import org.springframework.web.bind.annotation.RestController;

import com.zawed.api.network.Agents.AgentStatus;
import com.zawed.api.network.Agents.WithPin;
import com.zawed.api.network.Devices.DeviceStatus;
import com.zawed.api.network.Devices.Registered;
import com.zawed.api.network.NetworkApi.AgentPinResponse;
import com.zawed.api.network.NetworkApi.AgentResponse;
import com.zawed.api.network.NetworkApi.AgentStatusRequest;
import com.zawed.api.network.NetworkApi.CreateAgentRequest;
import com.zawed.api.network.NetworkApi.CreateStationRequest;
import com.zawed.api.network.NetworkApi.DeviceActivationResponse;
import com.zawed.api.network.NetworkApi.DeviceResponse;
import com.zawed.api.network.NetworkApi.OperatorBody;
import com.zawed.api.network.NetworkApi.OperatorResponse;
import com.zawed.api.network.NetworkApi.OperatorStatusRequest;
import com.zawed.api.network.NetworkApi.ReasonRequest;
import com.zawed.api.network.NetworkApi.RegisterDeviceRequest;
import com.zawed.api.network.NetworkApi.StationBody;
import com.zawed.api.network.NetworkApi.StationResponse;
import com.zawed.api.network.NetworkApi.StationStatusRequest;
import com.zawed.api.network.Operators.OperatorStatus;
import com.zawed.api.network.Operators.StationStatus;
import com.zawed.api.platform.security.Permission;
import com.zawed.api.platform.security.RequiresPermission;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiContract;
import com.zawed.api.platform.web.Page;

/** Station operators, stations, POS devices and agents in the Ops console (PRD §6.3). */
@RestController
@RequestMapping(ApiContract.BASE_PATH + "/ops")
@Tag(name = "network")
class NetworkController {

	private final Operators operators;
	private final Devices devices;
	private final Agents agents;

	NetworkController(Operators operators, Devices devices, Agents agents) {
		this.operators = operators;
		this.devices = devices;
		this.agents = agents;
	}

	// ------------------------------------------------------------------------------- operators

	@GetMapping("/operators")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "listOperators", summary = "Station operators, newest first")
	Page<OperatorResponse> operators(@RequestParam(required = false) OperatorStatus status,
			@RequestParam(required = false) String q, @RequestParam(required = false) String cursor,
			@RequestParam(required = false) Integer limit) {
		var page = operators.list(status, q, cursor, limit);
		return new Page<>(page.items().stream().map(OperatorResponse::of).toList(), page.nextCursor());
	}

	@PostMapping("/operators")
	@ResponseStatus(HttpStatus.CREATED)
	@RequiresPermission(Permission.NETWORK_MANAGE)
	@Operation(operationId = "createOperator", summary = "Onboard a station operator")
	OperatorResponse createOperator(@AuthenticationPrincipal ZawedPrincipal actor,
			@Valid @RequestBody OperatorBody body) {
		return OperatorResponse.of(operators.create(actor, body.draft()));
	}

	@GetMapping("/operators/{id}")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "getOperator", summary = "One station operator")
	OperatorResponse operator(@PathVariable UUID id) {
		return OperatorResponse.of(operators.get(id));
	}

	@PutMapping("/operators/{id}")
	@RequiresPermission(Permission.NETWORK_MANAGE)
	@Operation(operationId = "updateOperator", summary = "Edit a station operator")
	OperatorResponse updateOperator(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody OperatorBody body) {
		return OperatorResponse.of(operators.update(actor, id, body.draft()));
	}

	@PostMapping("/operators/{id}/status")
	@RequiresPermission(Permission.NETWORK_MANAGE)
	@Operation(operationId = "setOperatorStatus", summary = "Suspend or reactivate a station operator")
	OperatorResponse operatorStatus(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody OperatorStatusRequest body) {
		return OperatorResponse.of(operators.setStatus(actor, id, body.status(), body.reason().trim()));
	}

	// -------------------------------------------------------------------------------- stations

	@GetMapping("/stations")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "listStations", summary = "Stations, newest first")
	Page<StationResponse> stations(@RequestParam(required = false) UUID operatorId,
			@RequestParam(required = false) StationStatus status, @RequestParam(required = false) String q,
			@RequestParam(required = false) String cursor, @RequestParam(required = false) Integer limit) {
		var page = operators.stations(operatorId, status, q, cursor, limit);
		return new Page<>(page.items().stream().map(StationResponse::of).toList(), page.nextCursor());
	}

	@PostMapping("/stations")
	@ResponseStatus(HttpStatus.CREATED)
	@RequiresPermission(Permission.NETWORK_MANAGE)
	@Operation(operationId = "createStation", summary = "Add a station to an operator")
	StationResponse createStation(@AuthenticationPrincipal ZawedPrincipal actor,
			@Valid @RequestBody CreateStationRequest body) {
		return StationResponse.of(operators.createStation(actor, body.operatorId(), body.station().draft()));
	}

	@GetMapping("/stations/{id}")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "getStation", summary = "One station")
	StationResponse station(@PathVariable UUID id) {
		return StationResponse.of(operators.station(id));
	}

	@PutMapping("/stations/{id}")
	@RequiresPermission(Permission.NETWORK_MANAGE)
	@Operation(operationId = "updateStation", summary = "Edit a station")
	StationResponse updateStation(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody StationBody body) {
		return StationResponse.of(operators.updateStation(actor, id, body.draft()));
	}

	@PostMapping("/stations/{id}/status")
	@RequiresPermission(Permission.NETWORK_MANAGE)
	@Operation(operationId = "setStationStatus", summary = "Suspend, reactivate or close a station")
	StationResponse stationStatus(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody StationStatusRequest body) {
		return StationResponse.of(operators.setStationStatus(actor, id, body.status(), body.reason().trim()));
	}

	// --------------------------------------------------------------------------------- devices

	@GetMapping("/devices")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "listDevices", summary = "POS devices, newest first")
	Page<DeviceResponse> devices(@RequestParam(required = false) UUID operatorId,
			@RequestParam(required = false) UUID stationId, @RequestParam(required = false) DeviceStatus status,
			@RequestParam(required = false) String cursor, @RequestParam(required = false) Integer limit) {
		var page = devices.list(operatorId, stationId, status, cursor, limit);
		return new Page<>(page.items().stream().map(DeviceResponse::of).toList(), page.nextCursor());
	}

	@PostMapping("/devices")
	@RequiresPermission(Permission.NETWORK_MANAGE)
	@Operation(operationId = "registerDevice", summary = "Register a POS device; the activation code is shown once")
	ResponseEntity<DeviceActivationResponse> registerDevice(@AuthenticationPrincipal ZawedPrincipal actor,
			@Valid @RequestBody RegisterDeviceRequest body) {
		return secret(HttpStatus.CREATED, activation(devices.register(actor, body.stationId(), body.label())));
	}

	@GetMapping("/devices/{id}")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "getDevice", summary = "One POS device")
	DeviceResponse device(@PathVariable UUID id) {
		return DeviceResponse.of(devices.get(id));
	}

	@PostMapping("/devices/{id}/activation-code")
	@RequiresPermission(Permission.NETWORK_MANAGE)
	@Operation(operationId = "reissueDeviceActivation", summary = "A new activation code for a device not yet activated")
	ResponseEntity<DeviceActivationResponse> reissue(@AuthenticationPrincipal ZawedPrincipal actor,
			@PathVariable UUID id) {
		return secret(HttpStatus.OK, activation(devices.reissueActivation(actor, id)));
	}

	@PostMapping("/devices/{id}/revoke")
	@RequiresPermission(Permission.NETWORK_MANAGE)
	@Operation(operationId = "revokeDevice", summary = "Revoke a device for good")
	DeviceResponse revoke(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody ReasonRequest body) {
		return DeviceResponse.of(devices.revoke(actor, id, body.reason().trim()));
	}

	// ---------------------------------------------------------------------------------- agents

	@GetMapping("/agents")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "listAgents", summary = "Station agents, newest first; q matches the name or worker id")
	Page<AgentResponse> agents(@RequestParam(required = false) UUID operatorId,
			@RequestParam(required = false) UUID stationId, @RequestParam(required = false) AgentStatus status,
			@RequestParam(required = false) String q, @RequestParam(required = false) String cursor,
			@RequestParam(required = false) Integer limit) {
		var page = agents.list(operatorId, stationId, status, q, cursor, limit);
		return new Page<>(page.items().stream().map(AgentResponse::of).toList(), page.nextCursor());
	}

	@PostMapping("/agents")
	@RequiresPermission(Permission.NETWORK_MANAGE)
	@Operation(operationId = "createAgent", summary = "Add a station agent; the PIN is shown once")
	ResponseEntity<AgentPinResponse> createAgent(@AuthenticationPrincipal ZawedPrincipal actor,
			@Valid @RequestBody CreateAgentRequest body) {
		return secret(HttpStatus.CREATED, pin(agents.create(actor, body.stationId(), body.fullName(), body.phone())));
	}

	@GetMapping("/agents/{id}")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "getAgent", summary = "One station agent")
	AgentResponse agent(@PathVariable UUID id) {
		return AgentResponse.of(agents.get(id));
	}

	@PostMapping("/agents/{id}/reset-pin")
	@RequiresPermission(Permission.NETWORK_MANAGE)
	@Operation(operationId = "resetAgentPin", summary = "A new PIN, shown once; clears any lockout")
	ResponseEntity<AgentPinResponse> resetPin(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id) {
		return secret(HttpStatus.OK, pin(agents.resetPin(actor, id)));
	}

	@PostMapping("/agents/{id}/status")
	@RequiresPermission(Permission.NETWORK_MANAGE)
	@Operation(operationId = "setAgentStatus", summary = "Suspend, reactivate or deactivate an agent")
	AgentResponse agentStatus(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody AgentStatusRequest body) {
		return AgentResponse.of(agents.setStatus(actor, id, body.status(), body.reason().trim()));
	}

	// ----------------------------------------------------------------------------------- helpers

	private static DeviceActivationResponse activation(Registered registered) {
		return new DeviceActivationResponse(DeviceResponse.of(registered.device()), registered.activationCode());
	}

	private static AgentPinResponse pin(WithPin withPin) {
		return new AgentPinResponse(AgentResponse.of(withPin.agent()), withPin.pin());
	}

	/** A response that carries a one-time secret must never be stored by a browser or a proxy. */
	private static <T> ResponseEntity<T> secret(HttpStatus status, T body) {
		return ResponseEntity.status(status).cacheControl(CacheControl.noStore()).body(body);
	}

}
