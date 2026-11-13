package com.zawed.api.network;

import java.math.BigDecimal;
import java.time.Instant;
import java.util.Set;
import java.util.UUID;

import jakarta.validation.constraints.DecimalMax;
import jakarta.validation.constraints.DecimalMin;
import jakarta.validation.constraints.Digits;
import jakarta.validation.constraints.Email;
import jakarta.validation.constraints.NotBlank;
import jakarta.validation.constraints.NotEmpty;
import jakarta.validation.constraints.NotNull;
import jakarta.validation.constraints.Size;

import io.swagger.v3.oas.annotations.media.Schema;
import io.swagger.v3.oas.annotations.media.Schema.RequiredMode;

import com.zawed.api.network.Agents.Agent;
import com.zawed.api.network.Agents.AgentStatus;
import com.zawed.api.network.Devices.Device;
import com.zawed.api.network.Devices.DeviceStatus;
import com.zawed.api.network.Operators.Connectivity;
import com.zawed.api.network.Operators.Operator;
import com.zawed.api.network.Operators.OperatorStatus;
import com.zawed.api.network.Operators.Station;
import com.zawed.api.network.Operators.StationStatus;
import com.zawed.api.platform.FuelType;

/** Request and response bodies of the network endpoints. */
final class NetworkApi {

	private NetworkApi() {
	}

	// ------------------------------------------------------------------------------- operators

	record OperatorBody(@NotBlank @Size(max = 200) String legalName, @NotBlank @Size(max = 200) String nameAr,
			@NotBlank @Size(max = 200) String nameEn, @Size(max = 50) String taxId,
			@Size(max = 50) String commercialRegistration, @Size(max = 200) String contactName,
			@Size(max = 30) String contactPhone, @Email @Size(max = 254) String contactEmail) {

		Operators.OperatorDraft draft() {
			return new Operators.OperatorDraft(legalName, nameAr, nameEn, taxId, commercialRegistration, contactName,
					contactPhone, contactEmail);
		}

	}

	record OperatorStatusRequest(@NotNull OperatorStatus status, @NotBlank @Size(max = 500) String reason) {
	}

	record OperatorResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) String legalName,
			@Schema(requiredMode = RequiredMode.REQUIRED) String nameAr,
			@Schema(requiredMode = RequiredMode.REQUIRED) String nameEn, String taxId, String commercialRegistration,
			String contactName, String contactPhone, String contactEmail,
			@Schema(requiredMode = RequiredMode.REQUIRED) OperatorStatus status,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant createdAt) {

		static OperatorResponse of(Operator o) {
			return new OperatorResponse(o.id(), o.legalName(), o.nameAr(), o.nameEn(), o.taxId(),
					o.commercialRegistration(), o.contactName(), o.contactPhone(), o.contactEmail(), o.status(),
					o.createdAt());
		}

	}

	// -------------------------------------------------------------------------------- stations

	record StationBody(@NotBlank @Size(max = 200) String nameAr, @NotBlank @Size(max = 200) String nameEn,
			@Size(max = 500) String address,
			@DecimalMin("-90") @DecimalMax("90") @Digits(integer = 2, fraction = 6) BigDecimal latitude,
			@DecimalMin("-180") @DecimalMax("180") @Digits(integer = 3, fraction = 6) BigDecimal longitude,
			@Size(max = 64) @Schema(description = "IANA zone; defaults to Africa/Cairo") String timeZone,
			@NotNull Connectivity connectivity, @NotEmpty Set<@NotNull FuelType> fuelTypes) {

		Operators.StationDraft draft() {
			return new Operators.StationDraft(nameAr, nameEn, address, latitude, longitude, timeZone, connectivity,
					fuelTypes);
		}

	}

	record CreateStationRequest(@NotNull UUID operatorId, @NotNull @jakarta.validation.Valid StationBody station) {
	}

	record StationStatusRequest(@NotNull StationStatus status, @NotBlank @Size(max = 500) String reason) {
	}

	record StationResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID operatorId,
			@Schema(requiredMode = RequiredMode.REQUIRED) String nameAr,
			@Schema(requiredMode = RequiredMode.REQUIRED) String nameEn, String address, BigDecimal latitude,
			BigDecimal longitude, @Schema(requiredMode = RequiredMode.REQUIRED) String timeZone,
			@Schema(requiredMode = RequiredMode.REQUIRED) Connectivity connectivity,
			@Schema(requiredMode = RequiredMode.REQUIRED) Set<FuelType> fuelTypes,
			@Schema(requiredMode = RequiredMode.REQUIRED) StationStatus status,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant createdAt) {

		static StationResponse of(Station s) {
			return new StationResponse(s.id(), s.operatorId(), s.nameAr(), s.nameEn(), s.address(), s.latitude(),
					s.longitude(), s.timeZone(), s.connectivity(), s.fuelTypes(), s.status(), s.createdAt());
		}

	}

	// --------------------------------------------------------------------------------- devices

	record RegisterDeviceRequest(@NotNull UUID stationId, @Size(max = 100) String label) {
	}

	record ReasonRequest(@NotBlank @Size(max = 500) String reason) {
	}

	record DeviceResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID operatorId,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID stationId,
			@Schema(requiredMode = RequiredMode.REQUIRED) String code, String label,
			@Schema(requiredMode = RequiredMode.REQUIRED) DeviceStatus status, Instant activationExpiresAt,
			Instant boundAt, Instant lastSyncAt, Instant revokedAt, String revokedReason,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant createdAt) {

		static DeviceResponse of(Device d) {
			return new DeviceResponse(d.id(), d.operatorId(), d.stationId(), d.code(), d.label(), d.status(),
					d.activationExpiresAt(), d.boundAt(), d.lastSyncAt(), d.revokedAt(), d.revokedReason(),
					d.createdAt());
		}

	}

	/** Returned once, when the code is generated. It cannot be read back later. */
	record DeviceActivationResponse(@Schema(requiredMode = RequiredMode.REQUIRED) DeviceResponse device,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "Shown once") String activationCode) {
	}

	// ---------------------------------------------------------------------------------- agents

	record CreateAgentRequest(@NotNull UUID stationId, @NotBlank @Size(max = 200) String fullName,
			@Size(max = 30) String phone) {
	}

	record AgentStatusRequest(@NotNull AgentStatus status, @NotBlank @Size(max = 500) String reason) {
	}

	record AgentResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID operatorId,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID stationId,
			@Schema(requiredMode = RequiredMode.REQUIRED) String workerId,
			@Schema(requiredMode = RequiredMode.REQUIRED) String fullName, String phone,
			@Schema(requiredMode = RequiredMode.REQUIRED) AgentStatus status,
			@Schema(requiredMode = RequiredMode.REQUIRED) boolean mustChangePin, Instant lockedUntil,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant createdAt) {

		static AgentResponse of(Agent a) {
			return new AgentResponse(a.id(), a.operatorId(), a.stationId(), a.workerId(), a.fullName(), a.phone(),
					a.status(), a.mustChangePin(), a.lockedUntil(), a.createdAt());
		}

	}

	/** Returned once, when the PIN is generated. It cannot be read back later. */
	record AgentPinResponse(@Schema(requiredMode = RequiredMode.REQUIRED) AgentResponse agent,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "Shown once") String pin) {
	}

}
