package com.zawed.api.fleet;

import static com.zawed.api.platform.persistence.Sql.instant;
import static com.zawed.api.platform.persistence.Sql.ts;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.math.BigDecimal;
import java.math.RoundingMode;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Clock;
import java.time.Instant;
import java.time.Year;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.HexFormat;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.UUID;

import org.dhatim.fastexcel.Workbook;
import org.dhatim.fastexcel.Worksheet;
import org.springframework.dao.DuplicateKeyException;
import org.springframework.http.HttpStatus;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import tools.jackson.core.type.TypeReference;
import tools.jackson.databind.ObjectMapper;

import com.zawed.api.audit.AuditLog;
import com.zawed.api.fleet.FleetImportFile.Column;
import com.zawed.api.fleet.FleetImportFile.Format;
import com.zawed.api.fleet.FleetImportFile.ImportRow;
import com.zawed.api.fleet.Vehicles.Tier;
import com.zawed.api.platform.FuelType;
import com.zawed.api.platform.ids.IdGenerator;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.platform.web.Page;
import com.zawed.api.tenancy.CompanyDirectory;

/**
 * Bulk vehicle import (audit F4, ADR 0026): Upload → Validate → Preview → Confirm → Background → Results.
 *
 * <p>The upload is parsed and every row validated in one short request that takes no locks: field
 * rules (the same ones as a single vehicle), duplicates inside the file, plates and fleet codes
 * already in service. Rows are stored normalised; the file is not kept. Confirming queues the job
 * for {@link FleetImportWorker}; in {@code STRICT} mode (the default) only a file without a single
 * problem may be confirmed, in {@code VALID_ROWS_ONLY} mode the problem rows are left out, and the
 * API and screen say so. Only one import per company runs at a time.
 */
@Service
class FleetImports {

	enum Status {

		VALIDATED, QUEUED, RUNNING, COMPLETED, COMPLETED_WITH_ERRORS, CANCELLED

	}

	enum Mode {

		STRICT, VALID_ROWS_ONLY

	}

	enum RowStatus {

		VALID, INVALID, DUPLICATE_IN_FILE, EXISTS, IMPORTED, FAILED, SKIPPED

	}

	record Job(UUID id, UUID companyId, String fileName, Format fileFormat, String fileSha256, Status status, Mode mode,
			int rowCount, int validCount, int invalidCount, int importedCount, int existingCount, int failedCount,
			UUID createdBy, Instant createdAt, UUID confirmedBy, Instant confirmedAt, Instant startedAt,
			Instant finishedAt) {

		static Job map(ResultSet rs, int n) throws SQLException {
			String mode = rs.getString("mode");
			return new Job(rs.getObject("id", UUID.class), rs.getObject("company_id", UUID.class), rs.getString("file_name"),
					Format.valueOf(rs.getString("file_format")), rs.getString("file_sha256"),
					Status.valueOf(rs.getString("status")), mode == null ? null : Mode.valueOf(mode), rs.getInt("row_count"),
					rs.getInt("valid_count"), rs.getInt("invalid_count"), rs.getInt("imported_count"),
					rs.getInt("existing_count"), rs.getInt("failed_count"), rs.getObject("created_by", UUID.class),
					instant(rs, "created_at"), rs.getObject("confirmed_by", UUID.class), instant(rs, "confirmed_at"),
					instant(rs, "started_at"), instant(rs, "finished_at"));
		}

	}

	record RowError(String column, String code) {
	}

	record Row(int rowNumber, String plate, String fleetCode, RowStatus status, List<RowError> errors, UUID vehicleId,
			Map<String, Object> data) {
	}

	/** A validated row's values, ready to become a vehicle. Money and volume in integers. */
	record Values(String plate, String fleetCode, String make, String model, Integer year, FuelType fuelType,
			long tankCapacityMl, UUID departmentId, Tier tier, Long vipMonthlyCeilingMl, Long dailyLimitMl,
			Long weeklyLimitMl, Long monthlyLimitMl) {
	}

	private final JdbcClient jdbc;
	private final CompanyDirectory companies;
	private final AuditLog audit;
	private final IdGenerator ids;
	private final Clock clock;
	private final ObjectMapper json;

	FleetImports(JdbcClient jdbc, CompanyDirectory companies, AuditLog audit, IdGenerator ids, Clock clock,
			ObjectMapper json) {
		this.jdbc = jdbc;
		this.companies = companies;
		this.audit = audit;
		this.ids = ids;
		this.clock = clock;
		this.json = json;
	}

	// ---------------------------------------------------------------------------- upload + validate

	@Transactional
	Job upload(ZawedPrincipal actor, UUID companyId, String fileName, byte[] bytes) {
		if (companies.find(companyId).isEmpty()) {
			throw ApiException.notFound("Company");
		}
		Format format = FleetImportFile.formatOf(fileName, bytes);
		List<ImportRow> rows = FleetImportFile.read(format, bytes);
		Map<String, UUID> departments = departments(companyId);

		// 1. Field rules, row by row.
		List<Validated> validated = new ArrayList<>();
		for (ImportRow row : rows) {
			validated.add(validate(row, departments));
		}
		// 2. Duplicates inside the file: the first occurrence stays, later ones are flagged.
		Set<String> plates = new HashSet<>();
		Set<String> codes = new HashSet<>();
		for (Validated v : validated) {
			if (v.plateKey() != null && !plates.add(v.plateKey())) {
				v.duplicate("plate", "DUPLICATE_PLATE_IN_FILE");
			}
			if (v.fleetCodeKey() != null && !codes.add(v.fleetCodeKey())) {
				v.duplicate("fleet_code", "DUPLICATE_FLEET_CODE_IN_FILE");
			}
		}
		// 3. Already in service: plates platform-wide, fleet codes within the company.
		Set<String> takenPlates = new HashSet<>(jdbc.sql("""
				select plate_key from vehicles where status <> 'DECOMMISSIONED' and plate_key = any(:keys)
				""").param("keys", plates.toArray(String[]::new)).query(String.class).list());
		Set<String> takenCodes = new HashSet<>(jdbc.sql("""
				select upper(fleet_code) from vehicles
				where company_id = :company and status <> 'DECOMMISSIONED' and upper(fleet_code) = any(:codes)
				""").param("company", companyId).param("codes", codes.toArray(String[]::new)).query(String.class).list());
		for (Validated v : validated) {
			if (v.status == RowStatus.VALID || v.status == RowStatus.INVALID) {
				if (v.plateKey() != null && takenPlates.contains(v.plateKey())) {
					v.exists("plate", "PLATE_IN_USE");
				}
				if (v.fleetCodeKey() != null && takenCodes.contains(v.fleetCodeKey())) {
					v.exists("fleet_code", "FLEET_CODE_IN_USE");
				}
			}
		}

		int valid = (int) validated.stream().filter((v) -> v.status == RowStatus.VALID).count();
		UUID id = ids.next();
		Instant now = clock.instant();
		jdbc.sql("""
				insert into fleet_import_jobs (id, company_id, file_name, file_sha256, file_format, status, row_count,
				                               valid_count, invalid_count, created_by, created_at, updated_at)
				values (:id, :company, :name, :sha, :format, 'VALIDATED', :rows, :valid, :invalid, :actor, :now, :now)
				""")
			.param("id", id)
			.param("company", companyId)
			.param("name", safeName(fileName))
			.param("sha", sha256(bytes))
			.param("format", format.name())
			.param("rows", validated.size())
			.param("valid", valid)
			.param("invalid", validated.size() - valid)
			.param("actor", actor.userId())
			.param("now", ts(now))
			.update();
		for (Validated v : validated) {
			jdbc.sql("""
					insert into fleet_import_rows (job_id, row_number, plate, plate_key, fleet_code, data, status, errors)
					values (:job, :row, :plate, :key, :code, cast(:data as jsonb), :status, cast(:errors as jsonb))
					""")
				.param("job", id)
				.param("row", v.number)
				.param("plate", v.raw.get(Column.PLATE))
				.param("key", v.plateKey())
				.param("code", v.raw.get(Column.FLEET_CODE))
				.param("data", write(v.values == null ? v.rawByKey() : v.values))
				.param("status", v.status.name())
				.param("errors", write(v.errors))
				.update();
		}
		audit.record(actorOf(actor), "FLEET_IMPORT_VALIDATED", "FLEET_IMPORT", id, Scope.company(companyId),
				Map.of("fileName", safeName(fileName), "rows", validated.size(), "valid", valid, "format", format.name()));
		return get(id);
	}

	/** One row being validated. */
	private static final class Validated {

		final int number;
		final Map<Column, String> raw;
		final List<RowError> errors = new ArrayList<>();
		RowStatus status = RowStatus.VALID;
		Values values;
		String plateKey;
		String fleetCodeKey;

		Validated(ImportRow row) {
			this.number = row.number();
			this.raw = row.cells();
		}

		String plateKey() {
			return plateKey;
		}

		String fleetCodeKey() {
			return fleetCodeKey;
		}

		void error(String column, String code) {
			errors.add(new RowError(column, code));
			status = RowStatus.INVALID;
			values = null;
		}

		void duplicate(String column, String code) {
			errors.add(new RowError(column, code));
			if (status == RowStatus.VALID) {
				status = RowStatus.DUPLICATE_IN_FILE;
			}
		}

		void exists(String column, String code) {
			errors.add(new RowError(column, code));
			if (status == RowStatus.VALID) {
				status = RowStatus.EXISTS;
			}
		}

		Map<String, String> rawByKey() {
			Map<String, String> out = new LinkedHashMap<>();
			raw.forEach((column, value) -> out.put(column.key, value));
			return out;
		}

	}

	private Validated validate(ImportRow row, Map<String, UUID> departments) {
		Validated v = new Validated(row);
		Map<Column, String> c = row.cells();
		for (Map.Entry<Column, String> cell : c.entrySet()) {
			if (cell.getValue().length() > FleetImportFile.MAX_CELL) {
				v.error(cell.getKey().key, "TOO_LONG");
			}
		}
		for (Column column : Column.values()) {
			if (column.required && c.get(column) == null) {
				v.error(column.key, "REQUIRED");
			}
		}
		String plate = c.get(Column.PLATE);
		if (plate != null) {
			String key = PlateKey.of(plate);
			if (key.isEmpty() || plate.length() > 20) {
				v.error("plate", "INVALID_PLATE");
			}
			else {
				v.plateKey = key;
			}
		}
		String fleetCode = c.get(Column.FLEET_CODE);
		if (fleetCode != null) {
			if (fleetCode.length() > 40) {
				v.error("fleet_code", "TOO_LONG");
			}
			else {
				v.fleetCodeKey = fleetCode.toUpperCase(Locale.ROOT);
			}
		}
		for (Column text : List.of(Column.MAKE, Column.MODEL)) {
			if (c.get(text) != null && c.get(text).length() > 60) {
				v.error(text.key, "TOO_LONG");
			}
		}
		Integer year = null;
		if (c.get(Column.YEAR) != null) {
			year = integer(c.get(Column.YEAR));
			if (year == null || year < 1950 || year > Year.now(clock).getValue() + 1) {
				v.error("year", "INVALID_YEAR");
			}
		}
		FuelType fuel = c.get(Column.FUEL_TYPE) == null ? null : fuel(c.get(Column.FUEL_TYPE));
		if (c.get(Column.FUEL_TYPE) != null && fuel == null) {
			v.error("fuel_type", "INVALID_FUEL_TYPE");
		}
		Long tank = c.get(Column.TANK_L) == null ? null : litres(c.get(Column.TANK_L));
		if (c.get(Column.TANK_L) != null && (tank == null || tank <= 0 || tank > Vehicles.MAX_TANK_ML)) {
			v.error("tank_capacity_l", "INVALID_TANK_CAPACITY");
		}
		UUID department = null;
		if (c.get(Column.DEPARTMENT) != null) {
			department = departments.get(FleetImportFile.normalize(c.get(Column.DEPARTMENT)));
			if (department == null) {
				v.error("department", "UNKNOWN_DEPARTMENT");
			}
		}
		Tier tier = Tier.NORMAL;
		if (c.get(Column.TIER) != null) {
			tier = tier(c.get(Column.TIER));
			if (tier == null) {
				v.error("tier", "INVALID_TIER");
			}
		}
		Long ceiling = null;
		if (c.get(Column.VIP_CEILING_L) != null) {
			ceiling = litres(c.get(Column.VIP_CEILING_L));
			if (ceiling == null || ceiling <= 0) {
				v.error("vip_monthly_ceiling_l", "INVALID_NUMBER");
			}
			else if (tier != Tier.VIP) {
				v.error("vip_monthly_ceiling_l", "VIP_CEILING_ONLY_FOR_VIP");
			}
		}
		Long[] limits = new Long[3];
		Column[] limitColumns = { Column.DAILY_L, Column.WEEKLY_L, Column.MONTHLY_L };
		for (int i = 0; i < 3; i++) {
			if (c.get(limitColumns[i]) != null) {
				limits[i] = litres(c.get(limitColumns[i]));
				if (limits[i] == null || limits[i] <= 0 || limits[i] > 100_000_000L) {
					v.error(limitColumns[i].key, "INVALID_LIMIT");
				}
			}
		}
		if (v.status == RowStatus.VALID) {
			v.values = new Values(plate, fleetCode, c.get(Column.MAKE), c.get(Column.MODEL), year, fuel, tank, department,
					tier, ceiling, limits[0], limits[1], limits[2]);
		}
		return v;
	}

	// ------------------------------------------------------------------------------------- reading

	@Transactional(readOnly = true)
	Job get(UUID id) {
		return jdbc.sql("select * from fleet_import_jobs where id = :id")
			.param("id", id)
			.query(Job::map)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Import"));
	}

	@Transactional(readOnly = true)
	Page<Job> list(UUID companyId, String cursor, Integer limit) {
		int size = Page.limit(limit);
		List<Job> rows = jdbc.sql("""
				select * from fleet_import_jobs
				where (cast(:company as uuid) is null or company_id = :company)
				  and (cast(:cursor as uuid) is null or id < :cursor)
				order by id desc limit :limit
				""")
			.param("company", companyId)
			.param("cursor", Page.cursor(cursor))
			.param("limit", size + 1)
			.query(Job::map)
			.list();
		return Page.of(rows, size, Job::id);
	}

	/** Rows in file order; a cursor is the last row number seen. */
	@Transactional(readOnly = true)
	List<Row> rows(UUID jobId, RowStatus status, boolean problemsOnly, int after, int limit) {
		get(jobId);
		return jdbc.sql("""
				select * from fleet_import_rows
				where job_id = :job and row_number > :after
				  and (cast(:status as text) is null or status = :status)
				  and (not :problems or status not in ('VALID', 'IMPORTED'))
				order by row_number limit :limit
				""")
			.param("job", jobId)
			.param("after", after)
			.param("status", status == null ? null : status.name())
			.param("problems", problemsOnly)
			.param("limit", Math.min(Math.max(limit, 1), 500))
			.query(this::mapRow)
			.list();
	}

	/** Whether this exact file was already imported for the company (a warning, not a refusal). */
	@Transactional(readOnly = true)
	boolean importedBefore(Job job) {
		return Boolean.TRUE.equals(jdbc.sql("""
				select exists (select 1 from fleet_import_jobs
				               where company_id = :company and file_sha256 = :sha and id <> :id
				                 and status in ('COMPLETED', 'COMPLETED_WITH_ERRORS'))
				""").param("company", job.companyId()).param("sha", job.fileSha256()).param("id", job.id())
			.query(Boolean.class)
			.single());
	}

	// --------------------------------------------------------------------------- confirm / cancel

	@Transactional
	Job confirm(ZawedPrincipal actor, UUID id, Mode mode) {
		Job job = lock(id);
		if (job.status() != Status.VALIDATED) {
			if (job.status() == Status.QUEUED || job.status() == Status.RUNNING || job.status().name().startsWith("COMPLETED")) {
				return job; // a repeated confirmation: already queued or done, exactly once
			}
			throw new ApiException(HttpStatus.CONFLICT, "IMPORT_NOT_CONFIRMABLE", "This import can no longer be confirmed",
					null, Map.of("status", job.status().name()));
		}
		Mode chosen = mode == null ? Mode.STRICT : mode;
		if (chosen == Mode.STRICT && job.invalidCount() > 0) {
			throw new ApiException(HttpStatus.CONFLICT, "IMPORT_HAS_PROBLEMS",
					"Some rows have problems: fix the file, or confirm importing only the valid rows", null,
					Map.of("invalid", job.invalidCount()));
		}
		if (job.validCount() == 0) {
			throw new ApiException(HttpStatus.CONFLICT, "IMPORT_NOTHING_TO_IMPORT", "No row can be imported", null, null);
		}
		Instant now = clock.instant();
		try {
			jdbc.sql("""
					update fleet_import_jobs set status = 'QUEUED', mode = :mode, confirmed_by = :actor,
					       confirmed_by_role = :role, confirmed_at = :now, updated_at = :now
					where id = :id and status = 'VALIDATED'
					""")
				.param("mode", chosen.name())
				.param("actor", actor.userId())
				.param("role", actor.role().name())
				.param("now", ts(now))
				.param("id", id)
				.update();
		}
		catch (DuplicateKeyException ex) {
			throw new ApiException(HttpStatus.CONFLICT, "IMPORT_ALREADY_RUNNING",
					"Another import for this company is running; wait for it to finish", null, null);
		}
		if (chosen == Mode.VALID_ROWS_ONLY) {
			jdbc.sql("update fleet_import_rows set status = 'SKIPPED', processed_at = :now where job_id = :job and status <> 'VALID'")
				.param("now", ts(now)).param("job", id).update();
		}
		audit.record(actorOf(actor), "FLEET_IMPORT_CONFIRMED", "FLEET_IMPORT", id, Scope.company(job.companyId()),
				Map.of("mode", chosen.name(), "valid", job.validCount(), "invalid", job.invalidCount()));
		return get(id);
	}

	@Transactional
	Job cancel(ZawedPrincipal actor, UUID id) {
		Job job = lock(id);
		if (job.status() != Status.VALIDATED) {
			throw new ApiException(HttpStatus.CONFLICT, "IMPORT_NOT_CANCELLABLE", "Only an unconfirmed import is cancelled",
					null, Map.of("status", job.status().name()));
		}
		jdbc.sql("update fleet_import_jobs set status = 'CANCELLED', updated_at = :now where id = :id")
			.param("now", ts(clock.instant())).param("id", id).update();
		audit.record(actorOf(actor), "FLEET_IMPORT_CANCELLED", "FLEET_IMPORT", id, Scope.company(job.companyId()), Map.of());
		return get(id);
	}

	// ---------------------------------------------------------------------- reports and templates

	/** The rows that need attention, as CSV for a spreadsheet; every cell defused against formulas. */
	@Transactional(readOnly = true)
	byte[] errorReport(UUID jobId) {
		StringBuilder csv = new StringBuilder("﻿row,plate,fleet_code,status,errors\r\n");
		int after = 0;
		List<Row> page;
		do {
			page = rows(jobId, null, true, after, 500);
			for (Row row : page) {
				String errors = String.join("; ", row.errors().stream().map((e) -> e.column() + ": " + e.code()).toList());
				csv.append(row.rowNumber()).append(',').append(field(row.plate())).append(',').append(field(row.fleetCode()))
					.append(',').append(row.status().name()).append(',').append(field(errors)).append("\r\n");
				after = row.rowNumber();
			}
		}
		while (page.size() == 500);
		return csv.toString().getBytes(StandardCharsets.UTF_8);
	}

	static byte[] template(Format format, boolean arabic) {
		List<String> headers = new ArrayList<>();
		for (Column column : Column.values()) {
			headers.add(arabic ? column.arabicHeader() : column.key);
		}
		List<String> example = List.of("1234 ط ب ع", "TRK-001", "Toyota", "Hilux", "2022", arabic ? "سولار" : "DIESEL",
				"80", arabic ? "التوزيع" : "Distribution", arabic ? "عادي" : "NORMAL", "", "60", "", "1200");
		if (format == Format.CSV) {
			StringBuilder csv = new StringBuilder("﻿");
			csv.append(String.join(",", headers.stream().map(FleetImports::field).toList())).append("\r\n");
			csv.append(String.join(",", example.stream().map(FleetImports::field).toList())).append("\r\n");
			return csv.toString().getBytes(StandardCharsets.UTF_8);
		}
		ByteArrayOutputStream out = new ByteArrayOutputStream();
		try {
			Workbook workbook = new Workbook(out, "ZAWED", "1.0");
			Worksheet sheet = workbook.newWorksheet(arabic ? "المركبات" : "Vehicles");
			for (int i = 0; i < headers.size(); i++) {
				sheet.value(0, i, headers.get(i));
				sheet.style(0, i).bold().set();
				sheet.value(1, i, example.get(i));
				sheet.width(i, 20);
			}
			Worksheet help = workbook.newWorksheet(arabic ? "تعليمات" : "Instructions");
			String[] lines = arabic ? new String[] { "احذف صف المثال قبل الرفع.", "الأعمدة المطلوبة: اللوحة، الماركة، الموديل، نوع الوقود، سعة التانك (لتر).",
					"نوع الوقود: بنزين 80، بنزين 92، بنزين 95، سولار، غاز طبيعي (أو GAS_80, GAS_92, GAS_95, DIESEL, CNG).",
					"الفئة: عادي أو VIP. الحدود باللتر.", "الإدارة: اسم إدارة موجودة في الشركة.", "حتى 5,000 مركبة في الملف." }
					: new String[] { "Delete the example row before uploading.",
							"Required: plate, make, model, fuel_type, tank_capacity_l.",
							"fuel_type: GAS_80, GAS_92, GAS_95, DIESEL, CNG (Arabic names accepted).",
							"tier: NORMAL or VIP. Limits are in litres.", "department: the name of an existing department.",
							"At most 5,000 vehicles per file." };
			for (int i = 0; i < lines.length; i++) {
				help.value(i, 0, lines[i]);
			}
			help.width(0, 100);
			workbook.finish();
		}
		catch (IOException ex) {
			throw new IllegalStateException(ex);
		}
		return out.toByteArray();
	}

	// ----------------------------------------------------------------------------------- helpers

	Values values(Map<String, Object> data) {
		return json.convertValue(data, Values.class);
	}

	private Row mapRow(ResultSet rs, int n) throws SQLException {
		List<RowError> errors = json.readValue(rs.getString("errors"), new TypeReference<List<RowError>>() {
		});
		Map<String, Object> data = json.readValue(rs.getString("data"), new TypeReference<Map<String, Object>>() {
		});
		return new Row(rs.getInt("row_number"), rs.getString("plate"), rs.getString("fleet_code"),
				RowStatus.valueOf(rs.getString("status")), errors, rs.getObject("vehicle_id", UUID.class), data);
	}

	private Job lock(UUID id) {
		return jdbc.sql("select * from fleet_import_jobs where id = :id for update")
			.param("id", id)
			.query(Job::map)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Import"));
	}

	private Map<String, UUID> departments(UUID companyId) {
		Map<String, UUID> byName = new HashMap<>();
		jdbc.sql("select id, name from departments where company_id = :company and status = 'ACTIVE'")
			.param("company", companyId)
			.query((rs, n) -> byName.put(FleetImportFile.normalize(rs.getString("name")), rs.getObject("id", UUID.class)))
			.list();
		return byName;
	}

	static FuelType fuel(String value) {
		String v = FleetImportFile.normalize(value).toUpperCase(Locale.ROOT);
		return switch (v) {
			case "GAS80", "80", "بنزين80" -> FuelType.GAS_80;
			case "GAS92", "92", "بنزين92" -> FuelType.GAS_92;
			case "GAS95", "95", "بنزين95" -> FuelType.GAS_95;
			case "DIESEL", "سولار", "ديزل", "سولر" -> FuelType.DIESEL;
			case "CNG", "غاز", "غازطبيعي" -> FuelType.CNG;
			default -> null;
		};
	}

	static Tier tier(String value) {
		String v = FleetImportFile.normalize(value).toUpperCase(Locale.ROOT);
		return switch (v) {
			case "NORMAL", "عادي", "عادى" -> Tier.NORMAL;
			case "VIP", "مميز" -> Tier.VIP;
			default -> null;
		};
	}

	/** Litres as typed (Western or Arabic digits, up to 3 decimals) to integer millilitres. */
	static Long litres(String value) {
		String digits = toWesternDigits(value).replace('٫', '.').replace(',', '.').strip();
		if (!digits.matches("\\d{1,9}(\\.\\d{1,3})?")) {
			return null;
		}
		return new BigDecimal(digits).movePointRight(3).setScale(0, RoundingMode.UNNECESSARY).longValueExact();
	}

	static Integer integer(String value) {
		String digits = toWesternDigits(value).strip();
		if (digits.matches("\\d{1,4}(\\.0+)?")) {
			return Integer.valueOf(digits.replaceAll("\\.0+$", ""));
		}
		return null;
	}

	private static String toWesternDigits(String value) {
		StringBuilder out = new StringBuilder(value.length());
		value.codePoints().forEach((c) -> {
			if (c >= 0x0660 && c <= 0x0669) {
				out.append((char) ('0' + c - 0x0660));
			}
			else if (c >= 0x06F0 && c <= 0x06F9) {
				out.append((char) ('0' + c - 0x06F0));
			}
			else {
				out.appendCodePoint(c);
			}
		});
		return out.toString();
	}

	/** A CSV field, quoted when needed and defused when a spreadsheet would read it as a formula. */
	static String field(String value) {
		if (value == null) {
			return "";
		}
		String v = value;
		if (!v.isEmpty() && "=+-@\t\r".indexOf(v.charAt(0)) >= 0) {
			v = "'" + v;
		}
		if (v.contains(",") || v.contains("\"") || v.contains("\n") || v.contains("\r")) {
			v = "\"" + v.replace("\"", "\"\"") + "\"";
		}
		return v;
	}

	private static String safeName(String fileName) {
		String name = fileName == null ? "import" : fileName.replaceAll("[\\p{Cntrl}/\\\\]", "_").strip();
		if (name.isEmpty()) {
			name = "import";
		}
		return name.length() > 255 ? name.substring(name.length() - 255) : name;
	}

	private static String sha256(byte[] bytes) {
		try {
			return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(bytes));
		}
		catch (NoSuchAlgorithmException ex) {
			throw new IllegalStateException(ex);
		}
	}

	private String write(Object value) {
		return json.writeValueAsString(value);
	}

	static AuditLog.Actor actorOf(ZawedPrincipal actor) {
		return new AuditLog.Actor(actor.userId(), actor.role());
	}

}
