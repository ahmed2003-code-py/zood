package com.zawed.api.pricing;

import java.time.Instant;
import java.util.List;
import java.util.UUID;

import jakarta.validation.Valid;
import jakarta.validation.constraints.Max;
import jakarta.validation.constraints.NotNull;
import jakarta.validation.constraints.Positive;
import jakarta.validation.constraints.Size;

import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.media.Schema;
import io.swagger.v3.oas.annotations.media.Schema.RequiredMode;
import io.swagger.v3.oas.annotations.tags.Tag;

import org.springframework.http.HttpStatus;
import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.ResponseStatus;
import org.springframework.web.bind.annotation.RestController;

import com.zawed.api.platform.FuelType;
import com.zawed.api.platform.security.Permission;
import com.zawed.api.platform.security.RequiresPermission;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiContract;
import com.zawed.api.platform.web.Page;
import com.zawed.api.pricing.Prices.Entry;

/** National fuel prices and station overrides in the Ops console. */
@RestController
@RequestMapping(ApiContract.BASE_PATH + "/ops/prices")
@Tag(name = "pricing")
class PricesController {

	record ScheduleRequest(@NotNull FuelType fuelType,
			@Schema(description = "Null for the national price") UUID stationId,
			@NotNull @Positive @Max(100_000) @Schema(description = "Piastres per litre") Long pricePiastres,
			@Schema(description = "Defaults to now; never in the past") Instant effectiveFrom,
			@Size(max = 500) String note) {
	}

	record PriceResponse(@Schema(requiredMode = RequiredMode.REQUIRED) FuelType fuelType, UUID stationId,
			@Schema(requiredMode = RequiredMode.REQUIRED) long pricePiastresPerLitre,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant effectiveFrom) {

		static PriceResponse of(PriceBook.Price p) {
			return new PriceResponse(p.fuelType(), p.stationId(), p.pricePiastresPerLitre(), p.effectiveFrom());
		}

	}

	record PriceEntryResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) FuelType fuelType, UUID stationId,
			@Schema(requiredMode = RequiredMode.REQUIRED) long pricePiastresPerLitre,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant effectiveFrom, String note,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant createdAt) {

		static PriceEntryResponse of(Entry e) {
			return new PriceEntryResponse(e.id(), e.fuelType(), e.stationId(), e.pricePiastres(), e.effectiveFrom(),
					e.note(), e.createdAt());
		}

	}

	private final Prices prices;

	PricesController(Prices prices) {
		this.prices = prices;
	}

	@GetMapping("/current")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "currentPrices", summary = "The price in force now for every fuel, nationally or at a station")
	List<PriceResponse> current(@RequestParam(required = false) UUID stationId) {
		return prices.current(stationId).stream().map(PriceResponse::of).toList();
	}

	@GetMapping
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "listPrices", summary = "Price history and scheduled prices, newest first")
	Page<PriceEntryResponse> history(@RequestParam(required = false) FuelType fuelType,
			@RequestParam(required = false) UUID stationId, @RequestParam(defaultValue = "false") boolean nationalOnly,
			@RequestParam(required = false) String cursor, @RequestParam(required = false) Integer limit) {
		Page<Entry> page = prices.history(fuelType, stationId, nationalOnly, cursor, limit);
		return new Page<>(page.items().stream().map(PriceEntryResponse::of).toList(), page.nextCursor());
	}

	@PostMapping
	@ResponseStatus(HttpStatus.CREATED)
	@RequiresPermission(Permission.PRICES_MANAGE)
	@Operation(operationId = "schedulePrice", summary = "Schedule a national price or a station override")
	PriceEntryResponse schedule(@AuthenticationPrincipal ZawedPrincipal actor, @Valid @RequestBody ScheduleRequest body) {
		return PriceEntryResponse.of(prices.schedule(actor, body.fuelType(), body.stationId(), body.pricePiastres(),
				body.effectiveFrom(), body.note()));
	}

}
