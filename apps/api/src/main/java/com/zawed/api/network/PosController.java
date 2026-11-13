package com.zawed.api.network;

import java.time.Instant;
import java.util.UUID;

import jakarta.servlet.http.HttpServletRequest;
import jakarta.validation.Valid;
import jakarta.validation.constraints.NotBlank;
import jakarta.validation.constraints.NotNull;
import jakarta.validation.constraints.Size;

import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.media.Schema;
import io.swagger.v3.oas.annotations.media.Schema.RequiredMode;
import io.swagger.v3.oas.annotations.tags.Tag;

import org.springframework.http.CacheControl;
import org.springframework.http.ResponseEntity;
import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

import com.zawed.api.network.AgentSessions.SignedIn;
import com.zawed.api.platform.web.ApiContract;
import com.zawed.api.platform.web.ClientInfo;

/**
 * The POS terminal's credentials (B4 contract §1, ADR 0010): activation, device tokens, and the
 * agent's sign-in on the device. Every answer that carries a credential is {@code no-store}.
 */
@RestController
@RequestMapping(ApiContract.BASE_PATH + "/pos")
@Tag(name = "pos")
class PosController {

	private final DeviceChannel channel;
	private final AgentSessions sessions;

	PosController(DeviceChannel channel, AgentSessions sessions) {
		this.channel = channel;
		this.sessions = sessions;
	}

	record ActivateRequest(@NotBlank @Size(max = 40) String deviceCode, @NotBlank @Size(max = 40) String activationCode,
			@Schema(description = "EC P-256 public key, X.509 SubjectPublicKeyInfo, base64") @NotBlank @Size(
					max = 512) String publicKey) {
	}

	record ActivateResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID deviceId,
			@Schema(requiredMode = RequiredMode.REQUIRED) String deviceCode,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID stationId) {
	}

	record DeviceTokenRequest(@NotNull UUID deviceId,
			@Schema(description = "Device time, Unix seconds") @NotNull Long timestamp,
			@NotBlank @Size(min = 16, max = 128) String nonce,
			@Schema(description = "ECDSA P-256 SHA-256 signature, DER, base64, over "
					+ "\"zawed-device-token\n{deviceId}\n{timestamp}\n{nonce}\"") @NotBlank @Size(
							max = 256) String signature) {
	}

	record DeviceTokenResponse(@Schema(requiredMode = RequiredMode.REQUIRED) String token,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant expiresAt) {
	}

	record AgentLoginRequest(@NotBlank @Size(max = 20) String workerId, @NotBlank @Size(max = 20) String pin) {
	}

	record PosAgentResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) String workerId,
			@Schema(requiredMode = RequiredMode.REQUIRED) String fullName,
			@Schema(requiredMode = RequiredMode.REQUIRED) boolean mustChangePin) {
	}

	record AgentLoginResponse(@Schema(requiredMode = RequiredMode.REQUIRED) String sessionToken,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant expiresAt,
			@Schema(requiredMode = RequiredMode.REQUIRED) PosAgentResponse agent) {
	}

	record ChangePinRequest(@NotBlank @Size(max = 20) String currentPin, @NotBlank @Size(max = 20) String newPin) {
	}

	@PostMapping("/activate")
	@Operation(operationId = "activatePosDevice", summary = "Redeem the one-time activation code and register the device key")
	ResponseEntity<ActivateResponse> activate(@Valid @RequestBody ActivateRequest body, HttpServletRequest request) {
		var device = channel.activate(body.deviceCode(), body.activationCode(), body.publicKey(),
				ClientInfo.address(request))
			.device();
		return noStore(new ActivateResponse(device.deviceId(), device.code(), device.stationId()));
	}

	@PostMapping("/token")
	@Operation(operationId = "issuePosDeviceToken", summary = "A 15-minute device token for a signed timestamp and nonce")
	ResponseEntity<DeviceTokenResponse> token(@Valid @RequestBody DeviceTokenRequest body) {
		var issued = channel.token(body.deviceId(), body.timestamp(), body.nonce(), body.signature());
		return noStore(new DeviceTokenResponse(issued.token(), issued.expiresAt()));
	}

	@PostMapping("/agents/login")
	@Operation(operationId = "posAgentLogin", summary = "Agent sign-in on this device with Worker ID and PIN")
	ResponseEntity<AgentLoginResponse> login(@AuthenticationPrincipal PosPrincipal principal,
			@Valid @RequestBody AgentLoginRequest body) {
		SignedIn signedIn = sessions.signIn(PosCredentials.deviceOf(principal), body.workerId(), body.pin());
		var agent = signedIn.agent();
		return noStore(new AgentLoginResponse(signedIn.sessionToken(), signedIn.expiresAt(),
				new PosAgentResponse(agent.agentId(), agent.workerId(), agent.fullName(), agent.mustChangePin())));
	}

	@PostMapping("/agents/pin")
	@Operation(operationId = "posAgentChangePin", summary = "The signed-in agent replaces their PIN")
	ResponseEntity<Void> changePin(@AuthenticationPrincipal PosPrincipal principal,
			@Valid @RequestBody ChangePinRequest body) {
		sessions.changePin(PosCredentials.deviceOf(principal), PosCredentials.agentOf(principal), body.currentPin(),
				body.newPin());
		return ResponseEntity.noContent().build();
	}

	@PostMapping("/agents/logout")
	@Operation(operationId = "posAgentLogout", summary = "End the agent's session on this device")
	ResponseEntity<Void> logout(HttpServletRequest request) {
		sessions.signOut(request.getHeader(PosCredentials.AGENT_HEADER).strip());
		return ResponseEntity.noContent().build();
	}

	private static <T> ResponseEntity<T> noStore(T body) {
		return ResponseEntity.ok().cacheControl(CacheControl.noStore()).body(body);
	}

}
