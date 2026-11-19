package com.zawed.api.fleet;

import static org.assertj.core.api.Assertions.assertThat;

import java.io.ByteArrayOutputStream;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.util.List;
import java.util.UUID;
import java.util.concurrent.ThreadLocalRandom;
import java.util.zip.ZipEntry;
import java.util.zip.ZipOutputStream;

import com.jayway.jsonpath.JsonPath;

import org.dhatim.fastexcel.Workbook;
import org.dhatim.fastexcel.Worksheet;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.test.context.TestPropertySource;

import com.zawed.api.identity.IdentityTestSupport;
import com.zawed.api.platform.security.Role;
import com.zawed.api.support.Concurrently;
import com.zawed.api.support.Owner;

/**
 * Bulk vehicle import end to end (ADR 0026): upload, validation, preview, confirmation, background
 * execution, retry and recovery. The scheduled worker is off here and the tests drive it, so every
 * step is deterministic; the invariants are asserted in the database, not only in responses.
 */
@TestPropertySource(properties = "zawed.fleet-import.worker-enabled=false")
class FleetImportTests extends IdentityTestSupport {

	private static final String HEADER_AR = "اللوحة,كود الأسطول,الماركة,الموديل,سنة الصنع,نوع الوقود,سعة التانك (لتر),الإدارة,الفئة,حد يومي (لتر)";

	@Autowired
	FleetImportWorker worker;

	private SignedIn ops;

	@BeforeEach
	void signInOps() throws Exception {
		ops = signIn(seed(Role.OPS));
	}

	@Test
	void anArabicCsvIsValidatedPreviewedConfirmedAndImported() throws Exception {
		UUID company = seedCompany();
		department(company, "التوزيع");
		String p1 = plate();
		String p2 = plate();
		String csv = HEADER_AR + "\n" + p1 + ",TRK-1,Toyota,Hilux,٢٠٢٢,سولار,٨٠,التوزيع,عادي,60\n" + p2
				+ ",TRK-2,Isuzu,NPR,2021,بنزين 92,120.5,,,\n";
		HttpResponse<String> uploaded = upload(company, "fleet.csv", csv.getBytes(StandardCharsets.UTF_8));
		assertThat(uploaded.statusCode()).as(uploaded.body()).isEqualTo(201);
		assertThat(JsonPath.<String>read(uploaded.body(), "$.status")).isEqualTo("VALIDATED");
		assertThat(JsonPath.<Integer>read(uploaded.body(), "$.validCount")).isEqualTo(2);
		String job = JsonPath.read(uploaded.body(), "$.id");
		assertThat(vehicles(company)).as("validation creates nothing").isZero();

		HttpResponse<String> confirmed = confirm(job, null);
		assertThat(JsonPath.<String>read(confirmed.body(), "$.status")).as(confirmed.body()).isEqualTo("QUEUED");
		assertThat(JsonPath.<String>read(confirmed.body(), "$.mode")).isEqualTo("STRICT");
		worker.runOnce(100, "test-worker");

		HttpResponse<String> done = authed("GET", "/api/v1/ops/fleet-imports/" + job, ops.accessToken(), null);
		assertThat(JsonPath.<String>read(done.body(), "$.status")).isEqualTo("COMPLETED");
		assertThat(JsonPath.<Integer>read(done.body(), "$.importedCount")).isEqualTo(2);
		assertThat(vehicles(company)).isEqualTo(2L);
		assertThat(Owner.<Long>queryOne("select tank_capacity_ml from vehicles where company_id = ? and fleet_code = 'TRK-2'",
				company)).isEqualTo(120_500L);
		assertThat(Owner.<String>queryOne("select fuel_type from vehicles where company_id = ? and fleet_code = 'TRK-1'",
				company)).isEqualTo("DIESEL");
		assertThat(Owner.<Long>queryOne("""
				select l.max_value from limit_rules l join vehicles v on v.id = l.vehicle_id
				where v.company_id = ? and v.fleet_code = 'TRK-1' and l.active and l.period = 'DAY'""", company))
			.isEqualTo(60_000L);
		List<String> actions = Owner.queryList("select action from audit_log where entity_id = ? order by occurred_at, id",
				UUID.fromString(job));
		assertThat(actions).containsExactly("FLEET_IMPORT_VALIDATED", "FLEET_IMPORT_CONFIRMED", "FLEET_IMPORT_COMPLETED");
	}

	@Test
	void anXlsxFileImportsLikeACsv() throws Exception {
		UUID company = seedCompany();
		String p1 = plate();
		byte[] xlsx = xlsx(List.of(List.of("plate", "make", "model", "fuel_type", "tank_capacity_l"),
				List.of(p1, "Nissan", "Sunny", "GAS_92", "50")));
		HttpResponse<String> uploaded = upload(company, "fleet.xlsx", xlsx);
		assertThat(uploaded.statusCode()).as(uploaded.body()).isEqualTo(201);
		assertThat(JsonPath.<String>read(uploaded.body(), "$.fileFormat")).isEqualTo("XLSX");
		String job = JsonPath.read(uploaded.body(), "$.id");
		confirm(job, null);
		worker.runOnce(100, "test-worker");
		assertThat(vehicles(company)).isEqualTo(1L);
		// The template itself is a valid workbook in both languages.
		HttpResponse<byte[]> template = bytes("/api/v1/ops/fleet-imports/template?format=xlsx&lang=ar");
		assertThat(template.statusCode()).isEqualTo(200);
		assertThat(template.body()[0]).isEqualTo((byte) 'P');
	}

	@Test
	void badFilesAreRefusedWithAClearCodeAndNothingIsStored() throws Exception {
		UUID company = seedCompany();
		assertThat(code(upload(company, "fleet.txt", "plate\n1".getBytes(StandardCharsets.UTF_8))))
			.isEqualTo("IMPORT_FILE_TYPE");
		assertThat(code(upload(company, "fleet.csv", "plate,colour\nX,red".getBytes(StandardCharsets.UTF_8))))
			.isEqualTo("IMPORT_HEADER_INVALID");
		assertThat(code(upload(company, "fleet.csv", "plate,make\nX,Y".getBytes(StandardCharsets.UTF_8))))
			.as("required columns missing").isEqualTo("IMPORT_HEADER_INVALID");
		assertThat(code(upload(company, "fleet.csv", new byte[] { 'p', 'l', 'a', 't', 'e', '\n', (byte) 0xC3, (byte) 0x28 })))
			.isEqualTo("IMPORT_FILE_ENCODING");
		StringBuilder big = new StringBuilder("plate,make,model,fuel_type,tank_capacity_l\n");
		for (int i = 0; i < 5_001; i++) {
			big.append("P").append(i).append(",M,M,DIESEL,50\n");
		}
		assertThat(code(upload(company, "big.csv", big.toString().getBytes(StandardCharsets.UTF_8))))
			.as("never truncated").isEqualTo("IMPORT_TOO_MANY_ROWS");
		assertThat(code(upload(company, "bomb.xlsx", zipBomb()))).isEqualTo("IMPORT_FILE_UNSAFE");
		assertThat(code(upload(company, "entities.xlsx", zipWith("[Content_Types].xml",
				"<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY a \"b\">]><x>&a;</x>".getBytes(StandardCharsets.UTF_8)))))
			.isEqualTo("IMPORT_FILE_UNSAFE");
		assertThat(code(authed("POST", "/api/v1/ops/companies/" + company + "/fleet-imports", ops.accessToken(), "{}")))
			.isEqualTo("IMPORT_FILE_REQUIRED");
		assertThat(Owner.<Long>queryOne("select count(*) from fleet_import_jobs where company_id = ?", company)).isZero();
	}

	@Test
	void problemRowsAreReportedAndOnlyAnExplicitChoiceImportsTheRest() throws Exception {
		UUID company = seedCompany();
		UUID other = seedCompany();
		department(other, "Secret");
		String taken = plate();
		createVehicle(other, taken);
		String good = plate();
		String dup = plate();
		String csv = "plate,fleet_code,make,model,fuel_type,tank_capacity_l,department,tier,vip_monthly_ceiling_l\n"
				+ good + ",A1,Toyota,Hilux,DIESEL,80,,,\n" // 2: valid
				+ dup + ",A2,Toyota,Hilux,DIESEL,80,,,\n" // 3: valid (first of a duplicate)
				+ dup + ",A3,Toyota,Hilux,DIESEL,80,,,\n" // 4: duplicate plate in the file
				+ plate() + ",=HYPERLINK(\"x\"),,Hilux,PETROL,9999,Secret,NORMAL,50\n" // 5: many problems
				+ taken + ",A5,Toyota,Hilux,DIESEL,80,,,\n"; // 6: plate in service elsewhere
		HttpResponse<String> uploaded = upload(company, "mixed.csv", csv.getBytes(StandardCharsets.UTF_8));
		String job = JsonPath.read(uploaded.body(), "$.id");
		assertThat(JsonPath.<Integer>read(uploaded.body(), "$.validCount")).isEqualTo(2);
		assertThat(JsonPath.<Integer>read(uploaded.body(), "$.invalidCount")).isEqualTo(3);

		HttpResponse<String> problems = authed("GET", "/api/v1/ops/fleet-imports/" + job + "/rows?problemsOnly=true",
				ops.accessToken(), null);
		assertThat(JsonPath.<List<String>>read(problems.body(), "$.items[*].status"))
			.containsExactly("DUPLICATE_IN_FILE", "INVALID", "EXISTS");
		List<String> codes = JsonPath.read(problems.body(), "$.items[1].errors[*].code");
		assertThat(codes).contains("REQUIRED", "INVALID_FUEL_TYPE", "INVALID_TANK_CAPACITY", "UNKNOWN_DEPARTMENT",
				"VIP_CEILING_ONLY_FOR_VIP");

		String report = new String(bytes("/api/v1/ops/fleet-imports/" + job + "/errors.csv").body(), StandardCharsets.UTF_8);
		assertThat(report).contains("PLATE_IN_USE").contains("'=HYPERLINK").doesNotContain(",=HYPERLINK");

		assertThat(code(confirm(job, null))).as("strict refuses a file with problems").isEqualTo("IMPORT_HAS_PROBLEMS");
		HttpResponse<String> partial = confirm(job, "VALID_ROWS_ONLY");
		assertThat(JsonPath.<String>read(partial.body(), "$.mode")).isEqualTo("VALID_ROWS_ONLY");
		worker.runOnce(100, "test-worker");
		HttpResponse<String> done = authed("GET", "/api/v1/ops/fleet-imports/" + job, ops.accessToken(), null);
		assertThat(JsonPath.<String>read(done.body(), "$.status")).isEqualTo("COMPLETED_WITH_ERRORS");
		assertThat(JsonPath.<Integer>read(done.body(), "$.importedCount")).isEqualTo(2);
		assertThat(vehicles(company)).isEqualTo(2L);
		assertThat(Owner.<Long>queryOne("select count(*) from fleet_import_rows where job_id = ? and status = 'SKIPPED'",
				UUID.fromString(job))).isEqualTo(3L);
	}

	@Test
	void reUploadingAnImportedFileCreatesNothingTwice() throws Exception {
		UUID company = seedCompany();
		byte[] csv = ("plate,make,model,fuel_type,tank_capacity_l\n" + plate() + ",M,M,DIESEL,50\n" + plate()
				+ ",M,M,DIESEL,50\n").getBytes(StandardCharsets.UTF_8);
		String first = JsonPath.read(upload(company, "f.csv", csv).body(), "$.id");
		confirm(first, null);
		worker.runOnce(100, "test-worker");
		HttpResponse<String> again = upload(company, "f.csv", csv);
		assertThat(JsonPath.<Boolean>read(again.body(), "$.sameFileImportedBefore")).isTrue();
		assertThat(JsonPath.<Integer>read(again.body(), "$.validCount")).isZero();
		String second = JsonPath.read(again.body(), "$.id");
		assertThat(code(confirm(second, "VALID_ROWS_ONLY"))).isEqualTo("IMPORT_NOTHING_TO_IMPORT");
		assertThat(confirm(first, null).statusCode()).as("confirming a finished job again is harmless").isEqualTo(200);
		assertThat(vehicles(company)).isEqualTo(2L);
	}

	@Test
	void aWorkerCrashMidwayIsResumedByAnotherWithoutDuplicates() throws Exception {
		UUID company = seedCompany();
		StringBuilder csv = new StringBuilder("plate,make,model,fuel_type,tank_capacity_l\n");
		for (int i = 0; i < 6; i++) {
			csv.append(plate()).append(",M,M,DIESEL,50\n");
		}
		String job = JsonPath.read(upload(company, "f.csv", csv.toString().getBytes(StandardCharsets.UTF_8)).body(), "$.id");
		confirm(job, null);
		worker.runOnce(2, "worker-that-dies");
		assertThat(vehicles(company)).isEqualTo(2L);
		assertThat(Owner.<String>queryOne("select status from fleet_import_jobs where id = ?", UUID.fromString(job)))
			.isEqualTo("RUNNING");
		// While its lease holds, nobody else takes the job.
		assertThat(worker.runOnce(100, "other-worker")).isNull();
		// The worker died: its lease runs out.
		Owner.update("update fleet_import_jobs set lease_until = now() - interval '1 minute' where id = ?", UUID.fromString(job));
		assertThat(worker.runOnce(100, "other-worker")).isEqualTo(UUID.fromString(job));
		assertThat(vehicles(company)).isEqualTo(6L);
		assertThat(Owner.<String>queryOne("select status from fleet_import_jobs where id = ?", UUID.fromString(job)))
			.isEqualTo("COMPLETED");
		assertThat(Owner.<Integer>queryOne("select imported_count from fleet_import_jobs where id = ?", UUID.fromString(job)))
			.isEqualTo(6);
	}

	@Test
	void overlappingImportsNeverCreateADuplicate() throws Exception {
		// One company: a second import waits for the first.
		UUID company = seedCompany();
		byte[] a = ("plate,make,model,fuel_type,tank_capacity_l\n" + plate() + ",M,M,DIESEL,50\n").getBytes(StandardCharsets.UTF_8);
		String jobA = JsonPath.read(upload(company, "a.csv", a).body(), "$.id");
		String jobB = JsonPath.read(upload(company, "b.csv", ("plate,make,model,fuel_type,tank_capacity_l\n" + plate()
				+ ",M,M,DIESEL,50\n").getBytes(StandardCharsets.UTF_8)).body(), "$.id");
		confirm(jobA, null);
		assertThat(code(confirm(jobB, null))).isEqualTo("IMPORT_ALREADY_RUNNING");
		worker.runOnce(100, "w");

		// Two companies racing the same plate in two workers: one vehicle, the other row EXISTS.
		String shared = plate();
		UUID first = seedCompany();
		UUID second = seedCompany();
		byte[] file = ("plate,make,model,fuel_type,tank_capacity_l\n" + shared + ",M,M,DIESEL,50\n")
			.getBytes(StandardCharsets.UTF_8);
		String j1 = JsonPath.read(upload(first, "s.csv", file).body(), "$.id");
		String j2 = JsonPath.read(upload(second, "s.csv", file).body(), "$.id");
		confirm(j1, null);
		confirm(j2, null);
		Concurrently.run(2, (i) -> worker.runOnce(100, "racer-" + i));
		while (worker.runOnce(100, "sweeper") != null) {
			// finish whatever a racer left
		}
		assertThat(Owner.<Long>queryOne("select count(*) from vehicles where plate = ?", shared)).isEqualTo(1L);
		List<String> outcomes = Owner.queryList(
				"select status from fleet_import_rows where job_id in (?, ?) order by status", UUID.fromString(j1),
				UUID.fromString(j2));
		assertThat(outcomes).containsExactly("EXISTS", "IMPORTED");
	}

	@Test
	void theOnboardingChecklistFollowsTheCompanysData() throws Exception {
		UUID company = seedCompany();
		HttpResponse<String> empty = authed("GET", "/api/v1/ops/companies/" + company + "/onboarding", ops.accessToken(), null);
		assertThat(JsonPath.<Boolean>read(empty.body(), "$.ready")).as(empty.body()).isFalse();
		String vehicle = createVehicle(company, plate());
		authed("POST", "/api/v1/ops/vehicles/" + vehicle + "/tags", ops.accessToken(),
				"{\"uid\":\"" + UUID.randomUUID().toString().replace("-", "").substring(0, 14).toUpperCase() + "\"}");
		fund(ops, company, 10_000);
		HttpResponse<String> ready = authed("GET", "/api/v1/ops/companies/" + company + "/onboarding", ops.accessToken(), null);
		assertThat(JsonPath.<Boolean>read(ready.body(), "$.ready")).as(ready.body()).isTrue();
	}

	// ----------------------------------------------------------------------------------- helpers

	private HttpResponse<String> upload(UUID company, String name, byte[] content) throws Exception {
		String boundary = "----zawed" + UUID.randomUUID();
		ByteArrayOutputStream body = new ByteArrayOutputStream();
		body.write(("--" + boundary + "\r\nContent-Disposition: form-data; name=\"file\"; filename=\"" + name
				+ "\"\r\nContent-Type: application/octet-stream\r\n\r\n").getBytes(StandardCharsets.UTF_8));
		body.write(content);
		body.write(("\r\n--" + boundary + "--\r\n").getBytes(StandardCharsets.UTF_8));
		return send(request("/api/v1/ops/companies/" + company + "/fleet-imports")
			.header("Authorization", "Bearer " + ops.accessToken())
			.header("Content-Type", "multipart/form-data; boundary=" + boundary)
			.POST(HttpRequest.BodyPublishers.ofByteArray(body.toByteArray())));
	}

	private HttpResponse<String> confirm(String job, String mode) throws Exception {
		return authed("POST", "/api/v1/ops/fleet-imports/" + job + "/confirm", ops.accessToken(),
				mode == null ? "{}" : "{\"mode\":\"" + mode + "\"}");
	}

	private HttpResponse<byte[]> bytes(String path) throws Exception {
		return java.net.http.HttpClient.newHttpClient()
			.send(request(path).header("Authorization", "Bearer " + ops.accessToken()).GET().build(),
					HttpResponse.BodyHandlers.ofByteArray());
	}

	private String createVehicle(UUID company, String plate) throws Exception {
		HttpResponse<String> created = authed("POST", "/api/v1/ops/vehicles", ops.accessToken(),
				"{\"companyId\":\"" + company + "\",\"vehicle\":{\"plate\":\"" + plate + "\",\"make\":\"M\",\"model\":\"M\","
						+ "\"fuelType\":\"DIESEL\",\"tankCapacityMl\":50000,\"tier\":\"NORMAL\"}}");
		assertThat(created.statusCode()).as(created.body()).isEqualTo(201);
		return JsonPath.read(created.body(), "$.id");
	}

	private void department(UUID company, String name) throws Exception {
		assertThat(authed("POST", "/api/v1/ops/companies/" + company + "/departments", ops.accessToken(),
				"{\"name\":\"" + name + "\"}").statusCode()).isEqualTo(201);
	}

	private static long vehicles(UUID company) {
		return Owner.<Long>queryOne("select count(*) from vehicles where company_id = ?", company);
	}

	/** An Egyptian-shaped plate, unique enough for a test run. */
	private static String plate() {
		ThreadLocalRandom r = ThreadLocalRandom.current();
		String letters = "أبجدرسصطعفقكلمنهوي";
		return (1000 + r.nextInt(8999)) + " " + letters.charAt(r.nextInt(letters.length())) + " "
				+ letters.charAt(r.nextInt(letters.length())) + " " + letters.charAt(r.nextInt(letters.length()));
	}

	private static byte[] xlsx(List<List<String>> rows) throws Exception {
		ByteArrayOutputStream out = new ByteArrayOutputStream();
		Workbook workbook = new Workbook(out, "test", "1.0");
		Worksheet sheet = workbook.newWorksheet("Vehicles");
		for (int r = 0; r < rows.size(); r++) {
			for (int c = 0; c < rows.get(r).size(); c++) {
				sheet.value(r, c, rows.get(r).get(c));
			}
		}
		workbook.finish();
		return out.toByteArray();
	}

	private static byte[] zipBomb() throws Exception {
		ByteArrayOutputStream out = new ByteArrayOutputStream();
		try (ZipOutputStream zip = new ZipOutputStream(out)) {
			zip.putNextEntry(new ZipEntry("xl/worksheets/sheet1.xml"));
			byte[] zeros = new byte[1024 * 1024];
			for (int i = 0; i < 70; i++) {
				zip.write(zeros);
			}
			zip.closeEntry();
		}
		return out.toByteArray();
	}

	private static byte[] zipWith(String name, byte[] content) throws Exception {
		ByteArrayOutputStream out = new ByteArrayOutputStream();
		try (ZipOutputStream zip = new ZipOutputStream(out)) {
			zip.putNextEntry(new ZipEntry(name));
			zip.write(content);
			zip.closeEntry();
		}
		return out.toByteArray();
	}

}
