package com.zawed.api.fleet;

import java.io.ByteArrayInputStream;
import java.io.IOException;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.text.Normalizer;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.stream.Stream;

import org.apache.commons.compress.archivers.zip.ZipArchiveEntry;
import org.apache.commons.compress.archivers.zip.ZipFile;
import org.apache.commons.compress.utils.SeekableInMemoryByteChannel;
import org.dhatim.fastexcel.reader.Cell;
import org.dhatim.fastexcel.reader.ReadableWorkbook;
import org.dhatim.fastexcel.reader.Row;
import org.dhatim.fastexcel.reader.Sheet;
import org.springframework.http.HttpStatus;

import com.zawed.api.platform.web.ApiException;

/**
 * Reads an import file into rows of named columns, with hard limits and no surprises (ADR 0026).
 *
 * <ul>
 * <li>At most {@link #MAX_BYTES} on the wire and {@link #MAX_ROWS} data rows: a larger file is
 * refused with a clear error, never truncated.
 * <li>CSV: UTF-8 (a byte-order mark is fine), comma or semicolon, RFC 4180 quoting. Invalid UTF-8 is
 * refused rather than guessed.
 * <li>XLSX: the archive is inspected before parsing: at most {@link #MAX_ENTRIES} entries and
 * {@link #MAX_UNCOMPRESSED} bytes once inflated (a zip bomb stops here), and any XML with a
 * {@code <!DOCTYPE} is refused (no entity tricks). Only the first sheet is read, as text.
 * <li>Headers are matched by name, in English or Arabic, in any order; unknown columns are refused
 * so a typo never silently drops data.
 * </ul>
 */
final class FleetImportFile {

	private static final org.slf4j.Logger LOG = org.slf4j.LoggerFactory.getLogger(FleetImportFile.class);

	static final int MAX_BYTES = 5 * 1024 * 1024;

	static final int MAX_ROWS = 5_000;

	static final int MAX_COLUMNS = 30;

	static final int MAX_CELL = 200;

	static final int MAX_ENTRIES = 200;

	static final long MAX_UNCOMPRESSED = 60L * 1024 * 1024;

	enum Format {

		CSV, XLSX

	}

	/** The columns, by their canonical name, with the headers that mean them. */
	enum Column {

		PLATE("plate", true, "plate", "plate_number", "اللوحة", "رقم اللوحة"),
		FLEET_CODE("fleet_code", false, "fleet_code", "fleet code", "vehicle_code", "كود الأسطول", "الكود الداخلي",
				"كود العربية"),
		MAKE("make", true, "make", "brand", "الماركة", "الشركة المصنعة"),
		MODEL("model", true, "model", "الموديل"),
		YEAR("year", false, "year", "model_year", "سنة الصنع", "الموديل سنة"),
		FUEL_TYPE("fuel_type", true, "fuel_type", "fuel", "نوع الوقود", "الوقود"),
		TANK_L("tank_capacity_l", true, "tank_capacity_l", "tank_l", "tank", "سعة التانك (لتر)", "سعة التانك", "سعة الخزان"),
		DEPARTMENT("department", false, "department", "الإدارة", "القسم"),
		TIER("tier", false, "tier", "الفئة"),
		VIP_CEILING_L("vip_monthly_ceiling_l", false, "vip_monthly_ceiling_l", "سقف VIP الشهري (لتر)"),
		DAILY_L("daily_limit_l", false, "daily_limit_l", "حد يومي (لتر)", "الحد اليومي"),
		WEEKLY_L("weekly_limit_l", false, "weekly_limit_l", "حد أسبوعي (لتر)", "الحد الأسبوعي"),
		MONTHLY_L("monthly_limit_l", false, "monthly_limit_l", "حد شهري (لتر)", "الحد الشهري");

		final String key;

		final boolean required;

		final List<String> headers;

		Column(String key, boolean required, String... headers) {
			this.key = key;
			this.required = required;
			this.headers = List.of(headers);
		}

		/** The Arabic header the template uses (the last Arabic alias written first in the list). */
		String arabicHeader() {
			return headers.stream().filter((h) -> h.codePoints().anyMatch((c) -> c >= 0x0600 && c <= 0x06FF)).findFirst()
				.orElse(key);
		}

	}

	/** One data row: its number in the file (the header is row 1) and its cells by column. */
	record ImportRow(int number, Map<Column, String> cells) {
	}

	private FleetImportFile() {
	}

	static Format formatOf(String fileName, byte[] bytes) {
		boolean zip = bytes.length >= 4 && bytes[0] == 'P' && bytes[1] == 'K' && bytes[2] == 3 && bytes[3] == 4;
		String name = fileName == null ? "" : fileName.toLowerCase(Locale.ROOT);
		if (name.endsWith(".xlsx") && zip) {
			return Format.XLSX;
		}
		if (name.endsWith(".csv") && !zip) {
			return Format.CSV;
		}
		throw new ApiException(HttpStatus.BAD_REQUEST, "IMPORT_FILE_TYPE", "Upload a .csv or .xlsx file", "file", null);
	}

	static List<ImportRow> read(Format format, byte[] bytes) {
		if (bytes.length == 0) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "IMPORT_FILE_EMPTY", "The file is empty", "file", null);
		}
		if (bytes.length > MAX_BYTES) {
			throw tooLarge();
		}
		List<List<String>> grid = format == Format.CSV ? csv(bytes) : xlsx(bytes);
		if (grid.isEmpty()) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "IMPORT_FILE_EMPTY", "The file has no header row", "file", null);
		}
		List<Column> columns = header(grid.getFirst());
		List<ImportRow> rows = new ArrayList<>();
		for (int i = 1; i < grid.size(); i++) {
			List<String> cells = grid.get(i);
			if (cells.stream().allMatch((c) -> c == null || c.isBlank())) {
				continue; // blank lines carry nothing
			}
			Map<Column, String> named = new LinkedHashMap<>();
			for (int c = 0; c < columns.size(); c++) {
				String value = c < cells.size() ? clean(cells.get(c)) : null;
				if (value != null) {
					named.put(columns.get(c), value);
				}
			}
			rows.add(new ImportRow(i + 1, named));
		}
		if (rows.isEmpty()) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "IMPORT_FILE_EMPTY", "The file has no vehicle rows", "file", null);
		}
		if (rows.size() > MAX_ROWS) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "IMPORT_TOO_MANY_ROWS",
					"A file may hold at most 5,000 vehicles; split it", "file", Map.of("maxRows", MAX_ROWS));
		}
		return rows;
	}

	private static List<Column> header(List<String> raw) {
		if (raw.size() > MAX_COLUMNS) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "IMPORT_HEADER_INVALID", "Too many columns", "file", null);
		}
		List<Column> columns = new ArrayList<>();
		List<String> unknown = new ArrayList<>();
		for (String cell : raw) {
			String name = normalize(cell);
			Column match = null;
			for (Column column : Column.values()) {
				if (column.headers.stream().anyMatch((h) -> normalize(h).equals(name))) {
					match = column;
				}
			}
			if (match == null) {
				unknown.add(cell == null ? "" : cell.strip());
			}
			else if (columns.contains(match)) {
				throw new ApiException(HttpStatus.BAD_REQUEST, "IMPORT_HEADER_INVALID",
						"A column appears twice: " + match.key, "file", Map.of("column", match.key));
			}
			columns.add(match);
		}
		if (!unknown.isEmpty()) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "IMPORT_HEADER_INVALID",
					"Unknown columns; use the template's headers", "file", Map.of("unknown", unknown));
		}
		List<String> missing = new ArrayList<>();
		for (Column column : Column.values()) {
			if (column.required && !columns.contains(column)) {
				missing.add(column.key);
			}
		}
		if (!missing.isEmpty()) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "IMPORT_HEADER_INVALID", "Required columns are missing", "file",
					Map.of("missing", missing));
		}
		return columns;
	}

	/** Header comparison: NFKC, case-folded, spaces and underscores ignored. */
	static String normalize(String value) {
		if (value == null) {
			return "";
		}
		String folded = Normalizer.normalize(value, Normalizer.Form.NFKC).toLowerCase(Locale.ROOT).strip();
		if (!folded.isEmpty() && folded.charAt(0) == '﻿') {
			folded = folded.substring(1);
		}
		return folded.replaceAll("[\\s_\\-]+", "");
	}

	private static String clean(String value) {
		if (value == null) {
			return null;
		}
		String stripped = value.strip();
		if (stripped.isEmpty()) {
			return null;
		}
		return stripped.length() > MAX_CELL ? stripped.substring(0, MAX_CELL + 1) : stripped; // over-long is an error later
	}

	// ----------------------------------------------------------------------------------------- CSV

	private static List<List<String>> csv(byte[] bytes) {
		String text;
		try {
			text = StandardCharsets.UTF_8.newDecoder()
				.onMalformedInput(CodingErrorAction.REPORT)
				.onUnmappableCharacter(CodingErrorAction.REPORT)
				.decode(java.nio.ByteBuffer.wrap(bytes))
				.toString();
		}
		catch (CharacterCodingException ex) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "IMPORT_FILE_ENCODING",
					"Save the CSV as UTF-8 (Excel: CSV UTF-8)", "file", null);
		}
		if (text.startsWith("﻿")) {
			text = text.substring(1);
		}
		int firstLineEnd = text.indexOf('\n');
		String firstLine = firstLineEnd < 0 ? text : text.substring(0, firstLineEnd);
		char delimiter = firstLine.indexOf(',') < 0 && firstLine.indexOf(';') >= 0 ? ';' : ',';
		List<List<String>> rows = new ArrayList<>();
		List<String> row = new ArrayList<>();
		StringBuilder cell = new StringBuilder();
		boolean quoted = false;
		for (int i = 0; i < text.length(); i++) {
			char c = text.charAt(i);
			if (quoted) {
				if (c == '"') {
					if (i + 1 < text.length() && text.charAt(i + 1) == '"') {
						cell.append('"');
						i++;
					}
					else {
						quoted = false;
					}
				}
				else {
					cell.append(c);
				}
			}
			else if (c == '"' && cell.isEmpty()) {
				quoted = true;
			}
			else if (c == delimiter) {
				row.add(cell.toString());
				cell.setLength(0);
			}
			else if (c == '\n' || c == '\r') {
				if (c == '\r' && i + 1 < text.length() && text.charAt(i + 1) == '\n') {
					i++;
				}
				row.add(cell.toString());
				cell.setLength(0);
				rows.add(row);
				row = new ArrayList<>();
				guardRows(rows.size());
			}
			else {
				cell.append(c);
			}
			if (cell.length() > 10_000) {
				throw new ApiException(HttpStatus.BAD_REQUEST, "IMPORT_FILE_MALFORMED", "A cell is far too long", "file",
						null);
			}
		}
		if (quoted) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "IMPORT_FILE_MALFORMED", "A quoted value is never closed", "file",
					null);
		}
		if (!cell.isEmpty() || !row.isEmpty()) {
			row.add(cell.toString());
			rows.add(row);
		}
		return rows;
	}

	// ---------------------------------------------------------------------------------------- XLSX

	private static List<List<String>> xlsx(byte[] bytes) {
		inspectArchive(bytes);
		List<List<String>> rows = new ArrayList<>();
		try (ReadableWorkbook workbook = new ReadableWorkbook(new ByteArrayInputStream(bytes))) {
			Sheet sheet = workbook.getFirstSheet();
			try (Stream<Row> stream = sheet.openStream()) {
				stream.forEach((row) -> {
					guardRows(rows.size() + 1);
					List<String> cells = new ArrayList<>();
					int count = Math.min(row.getCellCount(), MAX_COLUMNS + 1);
					// Rows can skip empty cells; place each cell at its column.
					for (int i = 0; i < count; i++) {
						Cell cell = row.getCell(i);
						cells.add(cell == null ? null : cell.getText());
					}
					while (rows.size() + 1 < row.getRowNum()) {
						rows.add(List.of()); // a skipped (blank) row keeps the numbering true
						guardRows(rows.size());
					}
					rows.add(cells);
				});
			}
		}
		catch (ApiException ex) {
			throw ex;
		}
		catch (IOException | RuntimeException ex) {
			LOG.warn("An uploaded spreadsheet could not be read: {}", ex.toString());
			throw new ApiException(HttpStatus.BAD_REQUEST, "IMPORT_FILE_MALFORMED", "The spreadsheet could not be read",
					"file", null);
		}
		return rows;
	}

	/**
	 * Reads every entry once, counting real inflated bytes (not the sizes the archive claims); refuses
	 * bombs and DOCTYPEs. Uses the central directory, like the parser, so archives written in
	 * streaming mode (with data descriptors) are read correctly.
	 */
	private static void inspectArchive(byte[] bytes) {
		long total = 0;
		int entries = 0;
		byte[] buffer = new byte[16 * 1024];
		try (ZipFile zip = ZipFile.builder().setSeekableByteChannel(new SeekableInMemoryByteChannel(bytes)).get()) {
			java.util.Enumeration<ZipArchiveEntry> all = zip.getEntries();
			while (all.hasMoreElements()) {
				ZipArchiveEntry entry = all.nextElement();
				if (++entries > MAX_ENTRIES) {
					throw unsafe();
				}
				boolean xml = entry.getName().endsWith(".xml") || entry.getName().endsWith(".rels");
				StringBuilder head = new StringBuilder();
				try (java.io.InputStream in = zip.getInputStream(entry)) {
					int read;
					while ((read = in.read(buffer)) > 0) {
						total += read;
						if (total > MAX_UNCOMPRESSED) {
							throw unsafe();
						}
						if (xml && head.length() < 4096) {
							head.append(new String(buffer, 0, Math.min(read, 4096), StandardCharsets.ISO_8859_1));
						}
					}
				}
				if (xml && head.toString().toUpperCase(Locale.ROOT).contains("<!DOCTYPE")) {
					throw unsafe();
				}
			}
		}
		catch (ApiException ex) {
			throw ex;
		}
		catch (IOException | RuntimeException ex) {
			LOG.warn("An uploaded spreadsheet archive could not be read: {}", ex.toString());
			throw new ApiException(HttpStatus.BAD_REQUEST, "IMPORT_FILE_MALFORMED", "The spreadsheet could not be read",
					"file", null);
		}
		if (entries == 0) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "IMPORT_FILE_MALFORMED", "The spreadsheet is empty", "file", null);
		}
	}

	private static void guardRows(int rows) {
		if (rows > MAX_ROWS + 1 + 1000) { // generous room for blank lines, then stop reading at once
			throw new ApiException(HttpStatus.BAD_REQUEST, "IMPORT_TOO_MANY_ROWS",
					"A file may hold at most 5,000 vehicles; split it", "file", Map.of("maxRows", MAX_ROWS));
		}
	}

	private static ApiException unsafe() {
		return new ApiException(HttpStatus.BAD_REQUEST, "IMPORT_FILE_UNSAFE",
				"The spreadsheet is not a plain workbook (too large once unpacked, or with embedded definitions)", "file",
				null);
	}

	private static ApiException tooLarge() {
		return new ApiException(HttpStatus.PAYLOAD_TOO_LARGE, "IMPORT_FILE_TOO_LARGE", "A file may be at most 5 MB",
				"file", Map.of("maxBytes", MAX_BYTES));
	}

}
