package com.zawed.api.fueling;

import static com.zawed.api.platform.persistence.Sql.ts;

import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.time.Instant;
import java.time.ZoneId;
import java.time.format.DateTimeFormatter;
import java.util.List;
import java.util.UUID;

import org.springframework.http.HttpStatus;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import com.zawed.api.platform.web.ApiException;

/**
 * Fuelings as a CSV file for a spreadsheet (roadmap delta: "transaction export"). Runs in the
 * caller's scope, so row-level security limits the file exactly as it limits the list: a company
 * exports its own fuelings, an operator those at its stations.
 *
 * <p>The file is UTF-8 with a byte-order mark, so Excel shows Arabic names and plates correctly.
 * Money is in pounds with two decimals and volume in litres with two decimals, for a person to
 * read; times are in Africa/Cairo.
 */
@Service
class FuelingExports {

	/** A year at most, so one request cannot read the whole history. */
	static final Duration MAX_RANGE = Duration.ofDays(366);

	/** Far above a month of a large fleet; past it the range must be narrowed. */
	static final int MAX_ROWS = 50_000;

	private static final ZoneId CAIRO = ZoneId.of("Africa/Cairo");
	private static final DateTimeFormatter TIME = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm").withZone(CAIRO);
	private static final byte[] BOM = { (byte) 0xEF, (byte) 0xBB, (byte) 0xBF };

	private static final List<String> HEADER_EN = List.of("Reference", "Time (Cairo)", "Plate", "Company", "Station",
			"Fuel", "Litres", "Price per litre (EGP)", "Amount (EGP)", "Billable", "Status", "Decline reason",
			"Origin", "Conflicts", "Agent");

	private static final List<String> HEADER_AR = List.of("المرجع", "الوقت (القاهرة)", "اللوحة", "الشركة", "المحطة",
			"الوقود", "اللترات", "سعر اللتر (جنيه)", "المبلغ (جنيه)", "محسوب", "الحالة", "سبب الرفض", "المصدر",
			"التعارضات", "العامل");

	record Range(Instant from, Instant to) {

		Range {
			if (from == null || to == null) {
				throw new ApiException(HttpStatus.BAD_REQUEST, "VALIDATION_FAILED", "Both from and to are required", "from",
						null);
			}
			if (!to.isAfter(from)) {
				throw new ApiException(HttpStatus.BAD_REQUEST, "VALIDATION_FAILED", "to must be after from", "to", null);
			}
			if (Duration.between(from, to).compareTo(MAX_RANGE) > 0) {
				throw new ApiException(HttpStatus.BAD_REQUEST, "EXPORT_RANGE_TOO_LONG", "Export at most a year at a time",
						"to", java.util.Map.of("maxDays", MAX_RANGE.toDays()));
			}
		}

	}

	private final JdbcClient jdbc;

	FuelingExports(JdbcClient jdbc) {
		this.jdbc = jdbc;
	}

	/**
	 * @param companyId, operatorId, stationId, vehicleId filters; null does not filter
	 * @param arabic headers and names in Arabic instead of English
	 */
	@Transactional(readOnly = true)
	byte[] csv(Range range, UUID companyId, UUID operatorId, UUID stationId, UUID vehicleId, String status,
			boolean arabic) {
		long rows = jdbc.sql("""
				select count(*) from fuelings
				where evaluated_at >= :from and evaluated_at < :to
				  and (cast(:company as uuid) is null or company_id = :company)
				  and (cast(:operator as uuid) is null or operator_id = :operator)
				  and (cast(:station as uuid) is null or station_id = :station)
				  and (cast(:vehicle as uuid) is null or vehicle_id = :vehicle)
				  and (cast(:status as text) is null or status = :status)
				""")
			.param("from", ts(range.from()))
			.param("to", ts(range.to()))
			.param("company", companyId)
			.param("operator", operatorId)
			.param("station", stationId)
			.param("vehicle", vehicleId)
			.param("status", status)
			.query(Long.class)
			.single();
		if (rows > MAX_ROWS) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "EXPORT_TOO_LARGE", "Narrow the date range", "from",
					java.util.Map.of("rows", rows, "maxRows", MAX_ROWS));
		}
		StringBuilder out = new StringBuilder(256 * (int) rows + 512);
		line(out, arabic ? HEADER_AR : HEADER_EN);
		jdbc.sql("""
				select reference, evaluated_at, plate, company_name_ar, company_name_en, station_name_ar, station_name_en,
				       fuel_type, volume_ml, unit_price_piastres, amount_piastres, billable, status, decline_reason, origin,
				       conflicts, agent_worker_id
				from fuelings
				where evaluated_at >= :from and evaluated_at < :to
				  and (cast(:company as uuid) is null or company_id = :company)
				  and (cast(:operator as uuid) is null or operator_id = :operator)
				  and (cast(:station as uuid) is null or station_id = :station)
				  and (cast(:vehicle as uuid) is null or vehicle_id = :vehicle)
				  and (cast(:status as text) is null or status = :status)
				order by evaluated_at, reference
				""")
			.param("from", ts(range.from()))
			.param("to", ts(range.to()))
			.param("company", companyId)
			.param("operator", operatorId)
			.param("station", stationId)
			.param("vehicle", vehicleId)
			.param("status", status)
			.query((rs) -> {
				String[] conflicts = (String[]) rs.getArray("conflicts").getArray();
				line(out, List.of(rs.getString("reference"), TIME.format(rs.getTimestamp("evaluated_at").toInstant()),
						rs.getString("plate"), rs.getString(arabic ? "company_name_ar" : "company_name_en"),
						rs.getString(arabic ? "station_name_ar" : "station_name_en"), rs.getString("fuel_type"),
						decimal(rs.getLong("volume_ml"), 1000), decimal(rs.getLong("unit_price_piastres"), 100),
						decimal(rs.getLong("amount_piastres"), 100), rs.getBoolean("billable") ? "yes" : "no",
						rs.getString("status"), nullToEmpty(rs.getString("decline_reason")), rs.getString("origin"),
						String.join(" ", conflicts), rs.getString("agent_worker_id")));
			});
		byte[] text = out.toString().getBytes(StandardCharsets.UTF_8);
		byte[] file = new byte[BOM.length + text.length];
		System.arraycopy(BOM, 0, file, 0, BOM.length);
		System.arraycopy(text, 0, file, BOM.length, text.length);
		return file;
	}

	/** An integer amount in its minor unit as a plain decimal: 31500 piastres → 315.00. */
	static String decimal(long value, long unitsPerWhole) {
		long whole = Math.abs(value) / unitsPerWhole;
		long fraction = Math.abs(value) % unitsPerWhole;
		int digits = unitsPerWhole == 1000 ? 3 : 2;
		String text = whole + "." + String.format("%0" + digits + "d", fraction);
		if (digits == 3) {
			text = text.substring(0, text.length() - 1); // litres to two decimals; millilitre precision is not needed
		}
		return value < 0 ? "-" + text : text;
	}

	/** RFC 4180: quote a field holding a comma, quote or line break; double inner quotes. */
	private static void line(StringBuilder out, List<String> fields) {
		for (int i = 0; i < fields.size(); i++) {
			if (i > 0) {
				out.append(',');
			}
			String field = fields.get(i) == null ? "" : fields.get(i);
			// A leading =, +, - or @ would be read by a spreadsheet as a formula (CSV injection).
			if (!field.isEmpty() && "=+-@".indexOf(field.charAt(0)) >= 0 && !field.matches("-?\\d+(\\.\\d+)?")) {
				field = "'" + field;
			}
			if (field.contains(",") || field.contains("\"") || field.contains("\n") || field.contains("\r")) {
				out.append('"').append(field.replace("\"", "\"\"")).append('"');
			}
			else {
				out.append(field);
			}
		}
		out.append("\r\n");
	}

	private static String nullToEmpty(String value) {
		return value == null ? "" : value;
	}

}
