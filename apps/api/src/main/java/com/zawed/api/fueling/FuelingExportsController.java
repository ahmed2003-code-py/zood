package com.zawed.api.fueling;

import java.time.Instant;
import java.time.LocalDate;
import java.time.ZoneId;
import java.util.UUID;

import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.tags.Tag;

import org.springframework.http.CacheControl;
import org.springframework.http.ContentDisposition;
import org.springframework.http.HttpHeaders;
import org.springframework.http.MediaType;
import org.springframework.http.ResponseEntity;
import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.RestController;

import com.zawed.api.fueling.FuelingExports.Range;
import com.zawed.api.platform.security.Permission;
import com.zawed.api.platform.security.RequiresPermission;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiContract;

/**
 * Fuelings as a CSV file, for each audience the same rows its list shows (B6, roadmap delta:
 * transaction export). The range is two Cairo calendar days, inclusive: {@code from=2026-11-01&to=2026-11-30}.
 */
@RestController
@RequestMapping(ApiContract.BASE_PATH)
class FuelingExportsController {

	private static final ZoneId CAIRO = ZoneId.of("Africa/Cairo");
	private static final MediaType CSV = new MediaType("text", "csv", java.nio.charset.StandardCharsets.UTF_8);

	private final FuelingExports exports;

	FuelingExportsController(FuelingExports exports) {
		this.exports = exports;
	}

	@GetMapping("/ops/fuelings/export")
	@Tag(name = "fuelings")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "exportFuelings", summary = "Every fueling in a date range as CSV (Cairo days, inclusive)")
	ResponseEntity<byte[]> ops(@RequestParam(required = false) LocalDate from, @RequestParam(required = false) LocalDate to,
			@RequestParam(required = false) UUID companyId, @RequestParam(required = false) UUID operatorId,
			@RequestParam(required = false) UUID stationId, @RequestParam(required = false) UUID vehicleId,
			@RequestParam(required = false) String status, @RequestParam(defaultValue = "en") String lang) {
		return file("fuelings", from, to,
				exports.csv(range(from, to), companyId, operatorId, stationId, vehicleId, status, "ar".equals(lang)));
	}

	@GetMapping("/company/fuelings/export")
	@Tag(name = "company portal")
	@RequiresPermission(Permission.COMPANY_READ)
	@Operation(operationId = "exportMyFuelings", summary = "The company's fuelings in a date range as CSV")
	ResponseEntity<byte[]> company(@AuthenticationPrincipal ZawedPrincipal principal,
			@RequestParam(required = false) LocalDate from, @RequestParam(required = false) LocalDate to,
			@RequestParam(required = false) UUID vehicleId, @RequestParam(required = false) String status,
			@RequestParam(defaultValue = "en") String lang) {
		return file("fuelings", from, to, exports.csv(range(from, to), principal.scope().id(), null, null, vehicleId, status,
				"ar".equals(lang)));
	}

	@GetMapping("/station/fuelings/export")
	@Tag(name = "station portal")
	@RequiresPermission(Permission.STATION_READ)
	@Operation(operationId = "exportStationFuelings", summary = "Fuelings at the operator's stations in a date range as CSV")
	ResponseEntity<byte[]> station(@AuthenticationPrincipal ZawedPrincipal principal,
			@RequestParam(required = false) LocalDate from, @RequestParam(required = false) LocalDate to,
			@RequestParam(required = false) UUID stationId, @RequestParam(required = false) String status,
			@RequestParam(defaultValue = "en") String lang) {
		return file("fuelings", from, to, exports.csv(range(from, to), null, principal.scope().id(), stationId, null, status,
				"ar".equals(lang)));
	}

	/** From the start of the first Cairo day to the start of the day after the last. */
	private static Range range(LocalDate from, LocalDate to) {
		Instant start = from == null ? null : from.atStartOfDay(CAIRO).toInstant();
		Instant end = to == null ? null : to.plusDays(1).atStartOfDay(CAIRO).toInstant();
		return new Range(start, end);
	}

	private static ResponseEntity<byte[]> file(String name, LocalDate from, LocalDate to, byte[] body) {
		HttpHeaders headers = new HttpHeaders();
		headers.setContentDisposition(ContentDisposition.attachment().filename("zawed-" + name + "-" + from + "-" + to + ".csv").build());
		return ResponseEntity.ok().headers(headers).cacheControl(CacheControl.noStore()).contentType(CSV).body(body);
	}

}
