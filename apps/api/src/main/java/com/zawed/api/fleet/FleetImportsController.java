package com.zawed.api.fleet;

import java.io.IOException;
import java.time.Instant;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.media.Schema;
import io.swagger.v3.oas.annotations.media.Schema.RequiredMode;
import io.swagger.v3.oas.annotations.tags.Tag;

import org.springframework.http.ContentDisposition;
import org.springframework.http.HttpHeaders;
import org.springframework.http.HttpStatus;
import org.springframework.http.MediaType;
import org.springframework.http.ResponseEntity;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.transaction.annotation.Transactional;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.RestController;
import org.springframework.web.multipart.MultipartFile;

import com.zawed.api.fleet.FleetImportFile.Format;
import com.zawed.api.fleet.FleetImports.Job;
import com.zawed.api.fleet.FleetImports.Mode;
import com.zawed.api.fleet.FleetImports.Row;
import com.zawed.api.fleet.FleetImports.RowError;
import com.zawed.api.fleet.FleetImports.RowStatus;
import com.zawed.api.platform.security.Permission;
import com.zawed.api.platform.security.RequiresPermission;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiContract;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.platform.web.Page;
import com.zawed.api.tenancy.CompanyDirectory;

/**
 * Bulk vehicle import and company onboarding readiness in the Ops console (ADR 0026). The upload
 * takes its file as an optional part on purpose: the permission check runs before the body is read,
 * so a caller without the permission is told 403, not "file missing".
 */
@RestController
@RequestMapping(ApiContract.BASE_PATH + "/ops")
@Tag(name = "fleet onboarding")
class FleetImportsController {

	record FleetImportResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID companyId,
			@Schema(requiredMode = RequiredMode.REQUIRED) String fileName,
			@Schema(requiredMode = RequiredMode.REQUIRED) Format fileFormat,
			@Schema(requiredMode = RequiredMode.REQUIRED) FleetImports.Status status,
			@Schema(description = "STRICT: every row was valid. VALID_ROWS_ONLY: rows with problems were left out") Mode mode,
			@Schema(requiredMode = RequiredMode.REQUIRED) int rowCount,
			@Schema(requiredMode = RequiredMode.REQUIRED) int validCount,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "Rows with a problem, a duplicate in the file, or a plate or fleet code already in service") int invalidCount,
			@Schema(requiredMode = RequiredMode.REQUIRED) int importedCount,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "Rows whose plate or fleet code was registered by someone else while the import ran") int existingCount,
			@Schema(requiredMode = RequiredMode.REQUIRED) int failedCount,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant createdAt, Instant confirmedAt, Instant startedAt,
			Instant finishedAt,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "This exact file was already imported for the company") boolean sameFileImportedBefore) {

		static FleetImportResponse of(Job j, boolean importedBefore) {
			return new FleetImportResponse(j.id(), j.companyId(), j.fileName(), j.fileFormat(), j.status(), j.mode(),
					j.rowCount(), j.validCount(), j.invalidCount(), j.importedCount(), j.existingCount(), j.failedCount(),
					j.createdAt(), j.confirmedAt(), j.startedAt(), j.finishedAt(), importedBefore);
		}

	}

	record FleetImportRowError(@Schema(requiredMode = RequiredMode.REQUIRED) String column,
			@Schema(requiredMode = RequiredMode.REQUIRED) String code) {
	}

	record FleetImportRowResponse(@Schema(requiredMode = RequiredMode.REQUIRED) int rowNumber, String plate,
			String fleetCode, @Schema(requiredMode = RequiredMode.REQUIRED) RowStatus status,
			@Schema(requiredMode = RequiredMode.REQUIRED) List<FleetImportRowError> errors, UUID vehicleId) {

		static FleetImportRowResponse of(Row r) {
			return new FleetImportRowResponse(r.rowNumber(), r.plate(), r.fleetCode(), r.status(),
					r.errors().stream().map((RowError e) -> new FleetImportRowError(e.column(), e.code())).toList(),
					r.vehicleId());
		}

	}

	record FleetImportRowsResponse(@Schema(requiredMode = RequiredMode.REQUIRED) List<FleetImportRowResponse> items,
			@Schema(description = "Pass as after= for the next page; null at the end") Integer nextAfter) {
	}

	record ConfirmFleetImportRequest(@Schema(description = "STRICT (default): only a file without problems. VALID_ROWS_ONLY: import the valid rows and leave the others out") Mode mode) {
	}

	record OnboardingCheck(@Schema(requiredMode = RequiredMode.REQUIRED) String key,
			@Schema(requiredMode = RequiredMode.REQUIRED) boolean done,
			@Schema(requiredMode = RequiredMode.REQUIRED) boolean required, Long count) {
	}

	record OnboardingResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID companyId,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "Every required check is done: the company can fuel") boolean ready,
			@Schema(requiredMode = RequiredMode.REQUIRED) List<OnboardingCheck> checks) {
	}

	private final FleetImports imports;
	private final JdbcClient jdbc;
	private final CompanyDirectory companies;

	FleetImportsController(FleetImports imports, JdbcClient jdbc, CompanyDirectory companies) {
		this.imports = imports;
		this.jdbc = jdbc;
		this.companies = companies;
	}

	@GetMapping("/fleet-imports/template")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "downloadFleetImportTemplate", summary = "The vehicle import template, CSV or XLSX, in Arabic or English")
	ResponseEntity<byte[]> template(@RequestParam(defaultValue = "xlsx") String format,
			@RequestParam(defaultValue = "ar") String lang) {
		Format f = "csv".equalsIgnoreCase(format) ? Format.CSV : Format.XLSX;
		boolean arabic = !"en".equalsIgnoreCase(lang);
		String name = "zawed-vehicles-template-" + (arabic ? "ar" : "en") + (f == Format.CSV ? ".csv" : ".xlsx");
		MediaType type = f == Format.CSV ? new MediaType("text", "csv", java.nio.charset.StandardCharsets.UTF_8)
				: MediaType.parseMediaType("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
		return file(FleetImports.template(f, arabic), name, type);
	}

	@PostMapping("/companies/{id}/fleet-imports")
	@RequiresPermission(Permission.FLEET_MANAGE)
	@Operation(operationId = "uploadFleetImport",
			summary = "Upload a company's vehicle file (CSV or XLSX, at most 5 MB and 5,000 rows); validates every row and creates nothing yet")
	ResponseEntity<FleetImportResponse> upload(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@RequestParam(name = "file", required = false) MultipartFile file) throws IOException {
		if (file == null || file.isEmpty()) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "IMPORT_FILE_REQUIRED", "Attach the file as 'file'", "file",
					null);
		}
		if (file.getSize() > FleetImportFile.MAX_BYTES) {
			throw new ApiException(HttpStatus.PAYLOAD_TOO_LARGE, "IMPORT_FILE_TOO_LARGE", "A file may be at most 5 MB",
					"file", Map.of("maxBytes", FleetImportFile.MAX_BYTES));
		}
		Job job = imports.upload(actor, id, file.getOriginalFilename(), file.getBytes());
		return ResponseEntity.status(HttpStatus.CREATED).body(FleetImportResponse.of(job, imports.importedBefore(job)));
	}

	@GetMapping("/fleet-imports")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "listFleetImports", summary = "Imports, newest first; filter by company")
	Page<FleetImportResponse> list(@RequestParam(required = false) UUID companyId,
			@RequestParam(required = false) String cursor, @RequestParam(required = false) Integer limit) {
		Page<Job> page = imports.list(companyId, cursor, limit);
		return new Page<>(page.items().stream().map((j) -> FleetImportResponse.of(j, false)).toList(), page.nextCursor());
	}

	@GetMapping("/fleet-imports/{id}")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "getFleetImport", summary = "One import: its state and counts (poll it while it runs)")
	FleetImportResponse get(@PathVariable UUID id) {
		Job job = imports.get(id);
		return FleetImportResponse.of(job, imports.importedBefore(job));
	}

	@GetMapping("/fleet-imports/{id}/rows")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "listFleetImportRows", summary = "An import's rows in file order, with errors; problemsOnly for the preview")
	FleetImportRowsResponse rows(@PathVariable UUID id, @RequestParam(required = false) RowStatus status,
			@RequestParam(defaultValue = "false") boolean problemsOnly, @RequestParam(defaultValue = "0") int after,
			@RequestParam(defaultValue = "100") int limit) {
		int size = Math.min(Math.max(limit, 1), 500);
		List<Row> rows = imports.rows(id, status, problemsOnly, after, size);
		return new FleetImportRowsResponse(rows.stream().map(FleetImportRowResponse::of).toList(),
				rows.size() == size ? rows.getLast().rowNumber() : null);
	}

	@GetMapping("/fleet-imports/{id}/errors.csv")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "downloadFleetImportErrors", summary = "The rows that need attention, as CSV")
	ResponseEntity<byte[]> errors(@PathVariable UUID id) {
		return file(imports.errorReport(id), "zawed-import-" + id.toString().substring(0, 8) + "-errors.csv",
				new MediaType("text", "csv", java.nio.charset.StandardCharsets.UTF_8));
	}

	@PostMapping("/fleet-imports/{id}/confirm")
	@RequiresPermission(Permission.FLEET_MANAGE)
	@Operation(operationId = "confirmFleetImport", summary = "Queue a validated import; the vehicles are created in the background")
	FleetImportResponse confirm(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@RequestBody(required = false) ConfirmFleetImportRequest body) {
		Job job = imports.confirm(actor, id, body == null ? null : body.mode());
		return FleetImportResponse.of(job, imports.importedBefore(job));
	}

	@PostMapping("/fleet-imports/{id}/cancel")
	@RequiresPermission(Permission.FLEET_MANAGE)
	@Operation(operationId = "cancelFleetImport", summary = "Drop an import that was never confirmed")
	FleetImportResponse cancel(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id) {
		return FleetImportResponse.of(imports.cancel(actor, id), false);
	}

	// --------------------------------------------------------------------------------- readiness

	@GetMapping("/companies/{id}/onboarding")
	@RequiresPermission(Permission.OPS_READ)
	@Transactional(readOnly = true)
	@Operation(operationId = "getCompanyOnboarding",
			summary = "Whether a company is ready to fuel, derived from its data: details, policy, vehicles, tags, wallet, users")
	OnboardingResponse onboarding(@PathVariable UUID id) {
		if (companies.find(id).isEmpty()) {
			throw ApiException.notFound("Company");
		}
		Map<String, Object> facts = jdbc.sql("""
				select (c.contact_name is not null and (c.contact_phone is not null or c.contact_email is not null)) as contact,
				       (c.tax_id is not null or c.commercial_registration is not null) as registration,
				       c.status = 'ACTIVE' as active,
				       (select count(*) from departments d where d.company_id = c.id and d.status = 'ACTIVE') as departments,
				       (select count(*) from vehicles v where v.company_id = c.id and v.status = 'ACTIVE') as vehicles,
				       (select count(distinct t.vehicle_id) from rfid_tags t join vehicles v on v.id = t.vehicle_id
				         where t.company_id = c.id and t.status = 'ACTIVE' and v.status = 'ACTIVE') as tagged,
				       (select count(distinct l.vehicle_id) from limit_rules l where l.company_id = c.id and l.active) as limited,
				       coalesce((select w.balance_piastres from company_wallets w where w.company_id = c.id), -1) as balance,
				       (select count(*) from users u where u.scope_type = 'COMPANY' and u.scope_id = c.id
				         and u.status = 'ACTIVE') as users
				from companies c where c.id = :id
				""").param("id", id).query().singleRow();
		long vehicles = number(facts, "vehicles");
		long tagged = number(facts, "tagged");
		long balance = number(facts, "balance");
		List<OnboardingCheck> checks = List.of(
				new OnboardingCheck("ACTIVE", Boolean.TRUE.equals(facts.get("active")), true, null),
				new OnboardingCheck("CONTACT", Boolean.TRUE.equals(facts.get("contact")), false, null),
				new OnboardingCheck("REGISTRATION", Boolean.TRUE.equals(facts.get("registration")), false, null),
				new OnboardingCheck("DEPARTMENTS", number(facts, "departments") > 0, false, number(facts, "departments")),
				new OnboardingCheck("VEHICLES", vehicles > 0, true, vehicles),
				new OnboardingCheck("TAGS", vehicles > 0 && tagged == vehicles, true, tagged),
				new OnboardingCheck("LIMITS", number(facts, "limited") > 0, false, number(facts, "limited")),
				new OnboardingCheck("WALLET_FUNDED", balance > 0, true, Math.max(balance, 0)),
				new OnboardingCheck("COMPANY_USERS", number(facts, "users") > 0, false, number(facts, "users")));
		boolean ready = checks.stream().filter(OnboardingCheck::required).allMatch(OnboardingCheck::done);
		return new OnboardingResponse(id, ready, checks);
	}

	private static long number(Map<String, Object> facts, String key) {
		return ((Number) facts.get(key)).longValue();
	}

	private static ResponseEntity<byte[]> file(byte[] body, String name, MediaType type) {
		return ResponseEntity.ok()
			.contentType(type)
			.header(HttpHeaders.CONTENT_DISPOSITION, ContentDisposition.attachment().filename(name).build().toString())
			.header(HttpHeaders.CACHE_CONTROL, "no-store")
			.body(body);
	}

}
