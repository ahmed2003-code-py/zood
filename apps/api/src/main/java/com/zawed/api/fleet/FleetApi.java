package com.zawed.api.fleet;

import java.time.Instant;
import java.util.List;
import java.util.UUID;

import jakarta.validation.Valid;
import jakarta.validation.constraints.Max;
import jakarta.validation.constraints.Min;
import jakarta.validation.constraints.NotBlank;
import jakarta.validation.constraints.NotNull;
import jakarta.validation.constraints.Positive;
import jakarta.validation.constraints.Size;

import io.swagger.v3.oas.annotations.media.Schema;
import io.swagger.v3.oas.annotations.media.Schema.RequiredMode;

import com.zawed.api.fleet.Tags.Tag;
import com.zawed.api.fleet.Tags.TagStatus;
import com.zawed.api.fleet.Vehicles.LimitKind;
import com.zawed.api.fleet.Vehicles.LimitPeriod;
import com.zawed.api.fleet.Vehicles.LimitRule;
import com.zawed.api.fleet.Vehicles.Status;
import com.zawed.api.fleet.Vehicles.Tier;
import com.zawed.api.fleet.Vehicles.Vehicle;
import com.zawed.api.platform.FuelType;

/** Request and response bodies of the fleet endpoints. */
final class FleetApi {

	private FleetApi() {
	}

	record VehicleBody(UUID departmentId, @NotBlank @Size(max = 20) String plate, @NotBlank @Size(max = 60) String make,
			@NotBlank @Size(max = 60) String model, @Min(1950) @Max(2100) Integer year, @NotNull FuelType fuelType,
			@NotNull @Positive @Max(2_000_000) @Schema(description = "Millilitres") Long tankCapacityMl,
			@NotNull Tier tier, @Positive @Schema(description = "Millilitres a month; VIP only") Long vipMonthlyCeilingMl) {

		Vehicles.Draft draft() {
			return new Vehicles.Draft(departmentId, plate, make, model, year, fuelType, tankCapacityMl, tier,
					vipMonthlyCeilingMl);
		}

	}

	record CreateVehicleRequest(@NotNull UUID companyId, @NotNull @Valid VehicleBody vehicle) {
	}

	record VehicleStatusRequest(@NotNull Status status, @NotBlank @Size(max = 500) String reason) {
	}

	record LimitRequest(@NotNull LimitKind kind, @NotNull LimitPeriod period,
			@NotNull @Positive @Schema(description = "Millilitres for VOLUME, piastres for AMOUNT") Long maxValue) {
	}

	record IssueTagRequest(@NotBlank @Size(max = 64) @Schema(description = "Hex UID as read from the tag") String uid) {
	}

	record TagStatusRequest(@NotNull TagStatus status, @NotBlank @Size(max = 500) String reason) {
	}

	record VehicleResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID companyId, UUID departmentId,
			@Schema(requiredMode = RequiredMode.REQUIRED) String plate,
			@Schema(requiredMode = RequiredMode.REQUIRED) String make,
			@Schema(requiredMode = RequiredMode.REQUIRED) String model, Integer year,
			@Schema(requiredMode = RequiredMode.REQUIRED) FuelType fuelType,
			@Schema(requiredMode = RequiredMode.REQUIRED) long tankCapacityMl,
			@Schema(requiredMode = RequiredMode.REQUIRED) Tier tier, Long vipMonthlyCeilingMl,
			@Schema(requiredMode = RequiredMode.REQUIRED) Status status,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant createdAt) {

		static VehicleResponse of(Vehicle v) {
			return new VehicleResponse(v.id(), v.companyId(), v.departmentId(), v.plate(), v.make(), v.model(),
					v.year(), v.fuelType(), v.tankCapacityMl(), v.tier(), v.vipMonthlyCeilingMl(), v.status(),
					v.createdAt());
		}

		/**
		 * What the company sees: no VIP ceiling. The safeguard is invisible to the company by design
		 * (PRD §4.5): it only raises a flag for Operations.
		 */
		static VehicleResponse forCompany(Vehicle v) {
			return new VehicleResponse(v.id(), v.companyId(), v.departmentId(), v.plate(), v.make(), v.model(),
					v.year(), v.fuelType(), v.tankCapacityMl(), v.tier(), null, v.status(), v.createdAt());
		}

	}

	record TagResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID vehicleId,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "Only the last four characters are ever returned") String uidLast4,
			@Schema(requiredMode = RequiredMode.REQUIRED) TagStatus status, String statusReason, Instant cooldownUntil,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant createdAt) {

		static TagResponse of(Tag t) {
			return new TagResponse(t.id(), t.vehicleId(), t.uidLast4(), t.status(), t.statusReason(),
					t.cooldownUntil(), t.createdAt());
		}

	}

	record LimitResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) LimitKind kind,
			@Schema(requiredMode = RequiredMode.REQUIRED) LimitPeriod period,
			@Schema(requiredMode = RequiredMode.REQUIRED) long maxValue,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant createdAt) {

		static LimitResponse of(LimitRule r) {
			return new LimitResponse(r.id(), r.kind(), r.period(), r.maxValue(), r.createdAt());
		}

	}

	/** A vehicle with its tags and active limits: the detail screen in one call. */
	record VehicleDetail(@Schema(requiredMode = RequiredMode.REQUIRED) VehicleResponse vehicle,
			@Schema(requiredMode = RequiredMode.REQUIRED) List<TagResponse> tags,
			@Schema(requiredMode = RequiredMode.REQUIRED) List<LimitResponse> limits) {
	}

}
