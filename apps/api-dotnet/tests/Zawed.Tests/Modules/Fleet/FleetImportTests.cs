using System.IO.Compression;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Zawed.Api.Modules.Fleet;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Modules.Tenancy;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Fleet.FleetTestSupport;

namespace Zawed.Tests.Modules.Fleet;

/// <summary>
/// Bulk vehicle import end to end (ADR 0026; Java <c>FleetImportTests</c>): upload, validation, preview, confirmation,
/// background execution, retry and recovery. The background worker is off in every test application
/// (<see cref="WorkerOff"/>) and the tests drive it, so every step is deterministic; the invariants are asserted in the
/// database, not only in responses.
/// </summary>
public sealed class FleetImportTests(ProbeApi api) : IClassFixture<ProbeApi>
{
    private const string HeaderAr = "اللوحة,كود الأسطول,الماركة,الموديل,سنة الصنع,نوع الوقود,سعة التانك (لتر),الإدارة,الفئة,حد يومي (لتر)";

    private FleetImportWorker Worker => api.Factory.Services.GetRequiredService<FleetImportWorker>();

    [Fact]
    public async Task AnArabicCsvIsValidatedPreviewedConfirmedAndImported()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var company = await api.SeedCompanyAsync();
        await DepartmentAsync(ops, company, "التوزيع");
        var (p1, p2) = (EgyptianPlate(), EgyptianPlate());
        var csv = HeaderAr + "\n" + p1 + ",TRK-1,Toyota,Hilux,٢٠٢٢,سولار,٨٠,التوزيع,عادي,60\n" + p2 + ",TRK-2,Isuzu,NPR,2021,بنزين 92,120.5,,,\n";
        var uploaded = await UploadAsync(ops, company, "fleet.csv", Encoding.UTF8.GetBytes(csv));
        Assert.True(uploaded.StatusCode == HttpStatusCode.Created, await uploaded.Content.ReadAsStringAsync());
        var body = await uploaded.JsonAsync();
        Assert.Equal("VALIDATED", body.Text("status"));
        Assert.Equal(2, body.GetProperty("validCount").GetInt32());
        var job = body.Text("id")!;
        Assert.Equal(0L, await VehiclesAsync(company)); // validation creates nothing

        var confirmed = await ConfirmAsync(ops, job, null);
        Assert.Equal("QUEUED", (await confirmed.JsonAsync()).Text("status"));
        Assert.Equal("STRICT", (await confirmed.JsonAsync()).Text("mode"));
        await Worker.RunOnceAsync(100, "test-worker");

        var done = await (await api.GetAsync(ops, $"/api/v1/ops/fleet-imports/{job}")).JsonAsync();
        Assert.Equal("COMPLETED", done.Text("status"));
        Assert.Equal(2, done.GetProperty("importedCount").GetInt32());
        Assert.Equal(2L, await VehiclesAsync(company));
        Assert.Equal(120_500L, await api.Owner.QueryOneAsync<long>(
            "select tank_capacity_ml from vehicles where company_id = @company and fleet_code = 'TRK-2'", new { company }));
        Assert.Equal("DIESEL", await api.Owner.QueryOneAsync<string>(
            "select fuel_type from vehicles where company_id = @company and fleet_code = 'TRK-1'", new { company }));
        Assert.Equal(60_000L, await api.Owner.QueryOneAsync<long>("""
            select l.max_value from limit_rules l join vehicles v on v.id = l.vehicle_id
            where v.company_id = @company and v.fleet_code = 'TRK-1' and l.active and l.period = 'DAY'
            """, new { company }));
        var actions = await api.Owner.QueryAsync<string>("select action from audit_log where entity_id = @id order by occurred_at, id",
            new { id = Guid.Parse(job) });
        Assert.Equal(["FLEET_IMPORT_VALIDATED", "FLEET_IMPORT_CONFIRMED", "FLEET_IMPORT_COMPLETED"], actions);
    }

    [Fact]
    public async Task AnXlsxFileImportsLikeACsv()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var company = await api.SeedCompanyAsync();
        var xlsx = Workbook([["plate", "make", "model", "fuel_type", "tank_capacity_l"], [EgyptianPlate(), "Nissan", "Sunny", "GAS_92", "50"]]);
        var uploaded = await UploadAsync(ops, company, "fleet.xlsx", xlsx);
        Assert.True(uploaded.StatusCode == HttpStatusCode.Created, await uploaded.Content.ReadAsStringAsync());
        Assert.Equal("XLSX", (await uploaded.JsonAsync()).Text("fileFormat"));
        await ConfirmAsync(ops, (await uploaded.JsonAsync()).Text("id")!, null);
        await Worker.RunOnceAsync(100, "test-worker");
        Assert.Equal(1L, await VehiclesAsync(company));
        // The template itself is a valid workbook in both languages.
        var template = await api.GetAsync(ops, "/api/v1/ops/fleet-imports/template?format=xlsx&lang=ar");
        Assert.Equal(HttpStatusCode.OK, template.StatusCode);
        Assert.Equal((byte)'P', (await template.Content.ReadAsByteArrayAsync())[0]);
    }

    [Fact]
    public async Task BadFilesAreRefusedWithAClearCodeAndNothingIsStored()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var company = await api.SeedCompanyAsync();
        await Http.AssertErrorAsync(await UploadAsync(ops, company, "fleet.txt", "plate\n1"u8.ToArray()), 400, "IMPORT_FILE_TYPE");
        await Http.AssertErrorAsync(await UploadAsync(ops, company, "fleet.csv", "plate,colour\nX,red"u8.ToArray()), 400, "IMPORT_HEADER_INVALID");
        // Required columns missing.
        await Http.AssertErrorAsync(await UploadAsync(ops, company, "fleet.csv", "plate,make\nX,Y"u8.ToArray()), 400, "IMPORT_HEADER_INVALID");
        await Http.AssertErrorAsync(await UploadAsync(ops, company, "fleet.csv", [(byte)'p', (byte)'l', (byte)'a', (byte)'t', (byte)'e', (byte)'\n', 0xC3, 0x28]),
            400, "IMPORT_FILE_ENCODING");
        var big = new StringBuilder("plate,make,model,fuel_type,tank_capacity_l\n");
        for (var i = 0; i < 5_001; i++)
        {
            big.Append('P').Append(i).Append(",M,M,DIESEL,50\n");
        }
        // Never truncated.
        await Http.AssertErrorAsync(await UploadAsync(ops, company, "big.csv", Encoding.UTF8.GetBytes(big.ToString())), 400, "IMPORT_TOO_MANY_ROWS");
        await Http.AssertErrorAsync(await UploadAsync(ops, company, "bomb.xlsx", ZipBomb()), 400, "IMPORT_FILE_UNSAFE");
        await Http.AssertErrorAsync(await UploadAsync(ops, company, "entities.xlsx", ZipWith("[Content_Types].xml",
            "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY a \"b\">]><x>&a;</x>"u8.ToArray())), 400, "IMPORT_FILE_UNSAFE");
        // A DOCTYPE hidden behind more than one read buffer of comments, and one in a UTF-16 part.
        var padded = "<?xml version=\"1.0\"?><!--" + new string('x', 40_000) + "--><!doctype x [<!ENTITY a \"b\">]><x/>";
        await Http.AssertErrorAsync(await UploadAsync(ops, company, "late.xlsx", ZipWith("[Content_Types].xml", Encoding.UTF8.GetBytes(padded))),
            400, "IMPORT_FILE_UNSAFE");
        await Http.AssertErrorAsync(await UploadAsync(ops, company, "utf16.xlsx", ZipWith("[Content_Types].xml",
            Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-16\"?><x/>")).ToArray())),
            400, "IMPORT_FILE_UNSAFE");
        await Http.AssertErrorAsync(await api.PostAsync(ops, $"/api/v1/ops/companies/{company}/fleet-imports", "{}"), 400, "IMPORT_FILE_REQUIRED");
        Assert.Equal(0L, await api.Owner.QueryOneAsync<long>("select count(*) from fleet_import_jobs where company_id = @company", new { company }));
    }

    [Fact]
    public async Task ProblemRowsAreReportedAndOnlyAnExplicitChoiceImportsTheRest()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var company = await api.SeedCompanyAsync();
        var other = await api.SeedCompanyAsync();
        await DepartmentAsync(ops, other, "Secret");
        var taken = EgyptianPlate();
        Assert.Equal(HttpStatusCode.Created, (await api.CreateVehicleAsync(ops, other, taken)).StatusCode);
        var (good, dup) = (EgyptianPlate(), EgyptianPlate());
        var csv = "plate,fleet_code,make,model,fuel_type,tank_capacity_l,department,tier,vip_monthly_ceiling_l\n"
            + good + ",A1,Toyota,Hilux,DIESEL,80,,,\n" // 2: valid
            + dup + ",A2,Toyota,Hilux,DIESEL,80,,,\n" // 3: valid (first of a duplicate)
            + dup + ",A3,Toyota,Hilux,DIESEL,80,,,\n" // 4: duplicate plate in the file
            + EgyptianPlate() + ",=HYPERLINK(\"x\"),,Hilux,PETROL,9999,Secret,NORMAL,50\n" // 5: many problems
            + taken + ",A5,Toyota,Hilux,DIESEL,80,,,\n"; // 6: plate in service elsewhere
        var uploaded = await (await UploadAsync(ops, company, "mixed.csv", Encoding.UTF8.GetBytes(csv))).JsonAsync();
        var job = uploaded.Text("id")!;
        Assert.Equal(2, uploaded.GetProperty("validCount").GetInt32());
        Assert.Equal(3, uploaded.GetProperty("invalidCount").GetInt32());

        var problems = await (await api.GetAsync(ops, $"/api/v1/ops/fleet-imports/{job}/rows?problemsOnly=true")).JsonAsync();
        Assert.Equal(["DUPLICATE_IN_FILE", "INVALID", "EXISTS"], problems.Items("status"));
        var codes = problems.GetProperty("items")[1].GetProperty("errors").EnumerateArray().Select(e => e.Text("code")).ToList();
        foreach (var code in new[] { "REQUIRED", "INVALID_FUEL_TYPE", "INVALID_TANK_CAPACITY", "UNKNOWN_DEPARTMENT", "VIP_CEILING_ONLY_FOR_VIP" })
        {
            Assert.Contains(code, codes);
        }

        var report = await api.GetAsync(ops, $"/api/v1/ops/fleet-imports/{job}/errors.csv");
        Assert.Equal("text/csv; charset=UTF-8", report.Content.Headers.ContentType?.ToString(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal($"attachment; filename=\"zawed-import-{job[..8]}-errors.csv\"", report.Content.Headers.ContentDisposition?.ToString());
        var text = Encoding.UTF8.GetString(await report.Content.ReadAsByteArrayAsync());
        Assert.Contains("PLATE_IN_USE", text);
        Assert.Contains("'=HYPERLINK", text);
        Assert.DoesNotContain(",=HYPERLINK", text);

        // Strict refuses a file with problems.
        var strict = await Http.AssertErrorAsync(await ConfirmAsync(ops, job, null), 409, "IMPORT_HAS_PROBLEMS");
        Assert.Equal(3, strict.GetProperty("params").GetProperty("invalid").GetInt32());
        var partial = await ConfirmAsync(ops, job, "VALID_ROWS_ONLY");
        Assert.Equal("VALID_ROWS_ONLY", (await partial.JsonAsync()).Text("mode"));
        await Worker.RunOnceAsync(100, "test-worker");
        var done = await (await api.GetAsync(ops, $"/api/v1/ops/fleet-imports/{job}")).JsonAsync();
        Assert.Equal("COMPLETED_WITH_ERRORS", done.Text("status"));
        Assert.Equal(2, done.GetProperty("importedCount").GetInt32());
        Assert.Equal(2L, await VehiclesAsync(company));
        Assert.Equal(3L, await api.Owner.QueryOneAsync<long>(
            "select count(*) from fleet_import_rows where job_id = @id and status = 'SKIPPED'", new { id = Guid.Parse(job) }));
    }

    [Fact]
    public async Task ReUploadingAnImportedFileCreatesNothingTwice()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var company = await api.SeedCompanyAsync();
        var csv = Encoding.UTF8.GetBytes("plate,make,model,fuel_type,tank_capacity_l\n" + EgyptianPlate() + ",M,M,DIESEL,50\n" + EgyptianPlate()
            + ",M,M,DIESEL,50\n");
        var first = (await (await UploadAsync(ops, company, "f.csv", csv)).JsonAsync()).Text("id")!;
        await ConfirmAsync(ops, first, null);
        await Worker.RunOnceAsync(100, "test-worker");
        var again = await (await UploadAsync(ops, company, "f.csv", csv)).JsonAsync();
        Assert.True(again.GetProperty("sameFileImportedBefore").GetBoolean());
        Assert.Equal(0, again.GetProperty("validCount").GetInt32());
        await Http.AssertErrorAsync(await ConfirmAsync(ops, again.Text("id")!, "VALID_ROWS_ONLY"), 409, "IMPORT_NOTHING_TO_IMPORT");
        // Confirming a finished job again is harmless.
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(ops, first, null)).StatusCode);
        Assert.Equal(2L, await VehiclesAsync(company));
    }

    [Fact]
    public async Task AWorkerCrashMidwayIsResumedByAnotherWithoutDuplicates()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var company = await api.SeedCompanyAsync();
        var csv = new StringBuilder("plate,make,model,fuel_type,tank_capacity_l\n");
        for (var i = 0; i < 6; i++)
        {
            csv.Append(EgyptianPlate()).Append(",M,M,DIESEL,50\n");
        }
        var job = Guid.Parse((await (await UploadAsync(ops, company, "f.csv", Encoding.UTF8.GetBytes(csv.ToString()))).JsonAsync()).Text("id")!);
        await ConfirmAsync(ops, job.ToString(), null);
        await Worker.RunOnceAsync(2, "worker-that-dies");
        Assert.Equal(2L, await VehiclesAsync(company));
        Assert.Equal("RUNNING", await api.Owner.QueryOneAsync<string>("select status from fleet_import_jobs where id = @job", new { job }));
        // While its lease holds, nobody else takes the job.
        Assert.Null(await RunWorkerAsync("other-worker"));
        // The worker died: its lease runs out.
        await api.Owner.ExecuteAsync("update fleet_import_jobs set lease_until = now() - interval '1 minute' where id = @job", new { job });
        Assert.Equal(job, await RunWorkerAsync("other-worker"));
        Assert.Equal(6L, await VehiclesAsync(company));
        Assert.Equal("COMPLETED", await api.Owner.QueryOneAsync<string>("select status from fleet_import_jobs where id = @job", new { job }));
        Assert.Equal(6, await api.Owner.QueryOneAsync<int>("select imported_count from fleet_import_jobs where id = @job", new { job }));
    }

    [Fact]
    public async Task AControlCharacterInAFleetCodeIsRefusedAtValidation()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var company = await api.SeedCompanyAsync();
        // A tab or a line break is whitespace and is normalised to a space (Sql.Clean); a control character such as U+0001 is
        // not, and the database refuses it (V14), so validation must.
        var csv = "plate,fleet_code,make,model,fuel_type,tank_capacity_l\n" + EgyptianPlate() + ",TRK\u0001001,M,M,DIESEL,50\n" + EgyptianPlate()
            + ",TRK-002,M,M,DIESEL,50\n";
        var uploaded = await UploadAsync(ops, company, "codes.csv", Encoding.UTF8.GetBytes(csv));
        Assert.True(uploaded.StatusCode == HttpStatusCode.Created, await uploaded.Content.ReadAsStringAsync());
        var body = await uploaded.JsonAsync();
        Assert.Equal(1, body.GetProperty("validCount").GetInt32());
        Assert.Equal(1, body.GetProperty("invalidCount").GetInt32());
        Assert.Contains("INVALID_FLEET_CODE", await api.Owner.QueryOneAsync<string>(
            "select errors::text from fleet_import_rows where job_id = @id and status = 'INVALID'", new { id = Guid.Parse(body.Text("id")!) }));
    }

    [Fact]
    public async Task ARowThatFailsUnexpectedlyFailsOnItsOwnAndTheJobStillFinishes()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var company = await api.SeedCompanyAsync();
        var csv = new StringBuilder("plate,fleet_code,make,model,fuel_type,tank_capacity_l\n");
        for (var i = 1; i <= 3; i++)
        {
            csv.Append(EgyptianPlate()).Append(",C-").Append(i).Append(",M,M,DIESEL,50\n");
        }
        var id = Guid.Parse((await (await UploadAsync(ops, company, "f.csv", Encoding.UTF8.GetBytes(csv.ToString()))).JsonAsync()).Text("id")!);
        await ConfirmAsync(ops, id.ToString(), null);
        // A failure that is not a refusal: the stored row cannot be read back (an unknown fuel type).
        await api.Owner.ExecuteAsync("update fleet_import_rows set data = jsonb_set(data, '{fuelType}', to_jsonb('PLUTONIUM'::text)) "
            + "where job_id = @id and fleet_code = 'C-2'", new { id });

        Assert.Equal(id, await RunWorkerAsync("test-worker"));

        Assert.Equal("COMPLETED_WITH_ERRORS", await api.Owner.QueryOneAsync<string>("select status from fleet_import_jobs where id = @id", new { id }));
        Assert.Equal("FAILED", await api.Owner.QueryOneAsync<string>(
            "select status from fleet_import_rows where job_id = @id and fleet_code = 'C-2'", new { id }));
        Assert.Contains("INTERNAL", await api.Owner.QueryOneAsync<string>(
            "select errors::text from fleet_import_rows where job_id = @id and fleet_code = 'C-2'", new { id }));
        Assert.Equal(2L, await VehiclesAsync(company));
        // The counters agree with the rows.
        Assert.Equal(2, await api.Owner.QueryOneAsync<int>("select imported_count from fleet_import_jobs where id = @id", new { id }));
        Assert.Equal(1, await api.Owner.QueryOneAsync<int>("select failed_count from fleet_import_jobs where id = @id", new { id }));
        // Nothing is left for a worker to retry.
        Assert.Null(await RunWorkerAsync("test-worker"));
    }

    [Fact]
    public async Task TheJobCountersAreRecomputedFromTheRowsWhenItFinishes()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var company = await api.SeedCompanyAsync();
        var csv = "plate,make,model,fuel_type,tank_capacity_l\n" + EgyptianPlate() + ",M,M,DIESEL,50\n" + EgyptianPlate() + ",M,M,DIESEL,50\n";
        var job = Guid.Parse((await (await UploadAsync(ops, company, "f.csv", Encoding.UTF8.GetBytes(csv))).JsonAsync()).Text("id")!);
        await ConfirmAsync(ops, job.ToString(), null);
        await RunWorkerAsync("worker-a", 1);
        // Worker A loses its lease; a counter update it made after that would have matched no row.
        await api.Owner.ExecuteAsync("update fleet_import_jobs set imported_count = 0 where id = @job", new { job });
        await RunWorkerAsync("worker-a");
        Assert.Equal(2, await api.Owner.QueryOneAsync<int>("select imported_count from fleet_import_jobs where id = @job", new { job }));
    }

    [Fact]
    public async Task OverlappingImportsNeverCreateADuplicate()
    {
        var ops = await api.SeedAsync(Role.OPS);
        // One company: a second import waits for the first.
        var company = await api.SeedCompanyAsync();
        var jobA = (await (await UploadAsync(ops, company, "a.csv", Encoding.UTF8.GetBytes(
            "plate,make,model,fuel_type,tank_capacity_l\n" + EgyptianPlate() + ",M,M,DIESEL,50\n"))).JsonAsync()).Text("id")!;
        var jobB = (await (await UploadAsync(ops, company, "b.csv", Encoding.UTF8.GetBytes(
            "plate,make,model,fuel_type,tank_capacity_l\n" + EgyptianPlate() + ",M,M,DIESEL,50\n"))).JsonAsync()).Text("id")!;
        await ConfirmAsync(ops, jobA, null);
        await Http.AssertErrorAsync(await ConfirmAsync(ops, jobB, null), 409, "IMPORT_ALREADY_RUNNING");
        await RunWorkerAsync("w");

        // Two companies racing the same plate in two workers: one vehicle, the other row EXISTS.
        var shared = EgyptianPlate();
        var (first, second) = (await api.SeedCompanyAsync(), await api.SeedCompanyAsync());
        var file = Encoding.UTF8.GetBytes("plate,make,model,fuel_type,tank_capacity_l\n" + shared + ",M,M,DIESEL,50\n");
        var j1 = Guid.Parse((await (await UploadAsync(ops, first, "s.csv", file)).JsonAsync()).Text("id")!);
        var j2 = Guid.Parse((await (await UploadAsync(ops, second, "s.csv", file)).JsonAsync()).Text("id")!);
        await ConfirmAsync(ops, j1.ToString(), null);
        await ConfirmAsync(ops, j2.ToString(), null);
        await RaceAsync(2, i => Worker.RunOnceAsync(100, "racer-" + i));
        while (await Worker.RunOnceAsync(100, "sweeper") is not null)
        {
            // finish whatever a racer left
        }
        Assert.Equal(1L, await api.Owner.QueryOneAsync<long>("select count(*) from vehicles where plate = @shared", new { shared }));
        Assert.Equal(["EXISTS", "IMPORTED"], await api.Owner.QueryAsync<string>(
            "select status from fleet_import_rows where job_id in (@j1, @j2) order by status", new { j1, j2 }));
    }

    [Fact]
    public async Task TheOnboardingChecklistFollowsTheCompanysData()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var company = await api.SeedCompanyAsync();
        var empty = await (await api.GetAsync(ops, $"/api/v1/ops/companies/{company}/onboarding")).JsonAsync();
        Assert.False(empty.GetProperty("ready").GetBoolean());
        var vehicle = (await (await api.CreateVehicleAsync(ops, company, EgyptianPlate())).JsonAsync()).Text("id")!;
        await api.IssueTagAsync(ops, vehicle, RandomUid());
        await FundAsync(company, 10_000);
        var ready = await (await api.GetAsync(ops, $"/api/v1/ops/companies/{company}/onboarding")).JsonAsync();
        Assert.True(ready.GetProperty("ready").GetBoolean(), ready.ToString());
        var checks = ready.GetProperty("checks").EnumerateArray().ToDictionary(c => c.Text("key")!, c => c);
        Assert.Equal(["ACTIVE", "CONTACT", "REGISTRATION", "DEPARTMENTS", "VEHICLES", "TAGS", "LIMITS", "WALLET_FUNDED", "COMPANY_USERS"], checks.Keys);
        Assert.Equal(10_000, checks["WALLET_FUNDED"].GetProperty("count").GetInt64());
        Assert.Equal(JsonValueKind.Null, checks["ACTIVE"].GetProperty("count").ValueKind);
        await Http.AssertErrorAsync(await api.GetAsync(ops, $"/api/v1/ops/companies/{Guid.CreateVersion7()}/onboarding"), 404, "NOT_FOUND");
    }

    // ------------------------------------------------------------------------------------------------------
    // Beyond the Java class: the parser's edges, the template, cancelling, the company's view.

    [Fact]
    public async Task AWorkbookAsExcelWritesItIsReadCellByCell()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var company = await api.SeedCompanyAsync();
        var plate = EgyptianPlate();
        // Inline strings (one in two runs), number cells, a missing last cell, a blank row 2, the sheet named by its relationship.
        var sheet = "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>"
            + "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>plate</t></is></c><c r=\"B1\" t=\"inlineStr\"><is><r><t>ma</t></r><r><t>ke</t></r></is></c>"
            + "<c r=\"C1\" t=\"inlineStr\"><is><t>model</t></is></c><c r=\"D1\" t=\"inlineStr\"><is><t>fuel_type</t></is></c>"
            + "<c r=\"E1\" t=\"inlineStr\"><is><t>tank_capacity_l</t></is></c><c r=\"F1\" t=\"str\"><v>year</v></c>"
            + "<c r=\"G1\" t=\"inlineStr\"><is><t>fleet_code</t></is></c></row>"
            + $"<row r=\"3\"><c r=\"A3\" t=\"inlineStr\"><is><t>{plate}</t></is></c><c r=\"B3\" t=\"inlineStr\"><is><t>Kia</t></is></c>"
            + "<c r=\"C3\" t=\"inlineStr\"><is><t>Rio</t></is></c><c r=\"D3\" t=\"inlineStr\"><is><t>CNG</t></is></c>"
            + "<c r=\"E3\"><v>45.25</v></c><c r=\"F3\"><v>2020</v></c></row></sheetData></worksheet>";
        var workbook = ZipOf(new()
        {
            ["xl/worksheets/sheet7.xml"] = sheet,
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>",
            ["_rels/.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"r1\" "
                + "Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"/xl/workbook.xml\"/></Relationships>",
            ["xl/workbook.xml"] = "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
                + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"A\" sheetId=\"1\" r:id=\"x7\"/>"
                + "</sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship "
                + "Id=\"x7\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet7.xml\"/>"
                + "</Relationships>",
        });
        var uploaded = await UploadAsync(ops, company, "excel.XLSX", workbook);
        Assert.True(uploaded.StatusCode == HttpStatusCode.Created, await uploaded.Content.ReadAsStringAsync());
        var job = Guid.Parse((await uploaded.JsonAsync()).Text("id")!);
        Assert.Equal(1, (await uploaded.JsonAsync()).GetProperty("validCount").GetInt32());
        var data = await api.Owner.QueryOneAsync<string>("select data::text from fleet_import_rows where job_id = @job and row_number = 3", new { job });
        using var values = JsonDocument.Parse(data!);
        Assert.Equal(45_250, values.RootElement.GetProperty("tankCapacityMl").GetInt64());
        Assert.Equal(2020, values.RootElement.GetProperty("year").GetInt32());
        Assert.Equal("CNG", values.RootElement.GetProperty("fuelType").GetString());
        Assert.Equal(JsonValueKind.Null, values.RootElement.GetProperty("fleetCode").ValueKind);
        await Http.AssertErrorAsync(await UploadAsync(ops, company, "broken.xlsx", ZipWith("xl/workbook.xml", "<workbook"u8.ToArray())),
            400, "IMPORT_FILE_MALFORMED");
    }

    [Fact]
    public async Task TheTemplatesReadBackAsTheirOwnHeadersAndAnUnconfirmedImportIsCancelled()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var company = await api.SeedCompanyAsync();
        foreach (var (format, lang) in new[] { ("xlsx", "ar"), ("xlsx", "en"), ("csv", "ar"), ("csv", "en") })
        {
            var template = await api.GetAsync(ops, $"/api/v1/ops/fleet-imports/template?format={format}&lang={lang}");
            Assert.Equal($"attachment; filename=\"zawed-vehicles-template-{lang}.{format}\"", template.Content.Headers.ContentDisposition?.ToString());
            Assert.Equal("no-store", template.Headers.CacheControl?.ToString());
            // The example row is a valid vehicle once its department exists and its plate is free.
            var bytes = await template.Content.ReadAsByteArrayAsync();
            var rows = FleetImportFile.Read(format == "csv" ? ImportFormat.CSV : ImportFormat.XLSX, bytes);
            Assert.Equal("1234 ط ب ع", rows.Single().Get(ImportColumn.PLATE));
            Assert.Equal("1200", rows.Single().Get(ImportColumn.MONTHLY_L));
        }
        var job = (await (await UploadAsync(ops, company, "c.csv", Encoding.UTF8.GetBytes(
            "plate,make,model,fuel_type,tank_capacity_l\n" + EgyptianPlate() + ",M,M,DIESEL,50\n"))).JsonAsync()).Text("id")!;
        Assert.Equal("CANCELLED", (await (await api.PostAsync(ops, $"/api/v1/ops/fleet-imports/{job}/cancel", "")).JsonAsync()).Text("status"));
        var again = await Http.AssertErrorAsync(await api.PostAsync(ops, $"/api/v1/ops/fleet-imports/{job}/cancel", ""), 409, "IMPORT_NOT_CANCELLABLE");
        Assert.Equal("CANCELLED", again.GetProperty("params").Text("status"));
        await Http.AssertErrorAsync(await ConfirmAsync(ops, job, null), 409, "IMPORT_NOT_CONFIRMABLE");
        var list = await (await api.GetAsync(ops, $"/api/v1/ops/fleet-imports?companyId={company}")).JsonAsync();
        Assert.Equal([job], list.Items("id"));

        // Ops only: a company user is refused, and the database shows a company no import row at all.
        var manager = await api.SeedAsync(Role.FLEET_MANAGER, Scope.Company(company));
        await Http.AssertErrorAsync(await api.GetAsync(manager, $"/api/v1/ops/fleet-imports/{job}"), 403, "FORBIDDEN");
        Assert.Equal(0L, await api.Owner.AsRuntimeRoleAsync("COMPANY", company, manager.UserId, async (connection, tx) =>
            await Dapper.SqlMapper.QuerySingleAsync<long>(connection, "select count(*) from fleet_import_jobs", transaction: tx)));
    }

    [Fact]
    public async Task ThePermissionComesBeforeTheFileAndAnOversizedFileIs413()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var company = await api.SeedCompanyAsync();
        var manager = await api.SeedAsync(Role.FLEET_MANAGER, Scope.Company(company));
        await Http.AssertErrorAsync(await api.PostAsync(manager, $"/api/v1/ops/companies/{company}/fleet-imports", "{}"), 403, "FORBIDDEN");
        await Http.AssertErrorAsync(await UploadAsync(ops, company, "big.csv", new byte[FleetImportFile.MaxBytes + 1]), 413, "PAYLOAD_TOO_LARGE");
        await Http.AssertErrorAsync(await UploadAsync(ops, Guid.CreateVersion7(), "f.csv", "plate\nX"u8.ToArray()), 404, "NOT_FOUND");
        await Http.AssertErrorAsync(await UploadAsync(ops, company, "empty.csv", []), 400, "IMPORT_FILE_REQUIRED");
    }

    /// <summary>
    /// The multipart half of the Java <c>AuthorizationMatrixTests</c> (QA round 1 review): the permission comes before the
    /// upload is parsed, so a role without it is told 403 for a valid and for a broken body alike; an allowed caller's broken
    /// upload is the client's mistake, 400, not 500.
    /// </summary>
    [Fact]
    public async Task ARoleWithoutThePermissionIsRefusedBeforeAnUploadIsParsed()
    {
        var path = $"/api/v1/ops/companies/{Guid.CreateVersion7()}/fleet-imports";
        var allowed = Identity.AuthorizationMatrixTests.Matrix["POST /api/v1/ops/companies/{id}/fleet-imports"].Allowed.Roles;
        var boundary = "matrix" + Guid.NewGuid().ToString("N");
        var valid = $"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"f.csv\"\r\nContent-Type: text/csv\r\n\r\n"
            + $"plate,make\r\nA 1,M\r\n--{boundary}--\r\n";
        var broken = $"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"\r\n\r\nno end";
        Task<HttpResponseMessage> Send(ZawedPrincipal user, string body)
        {
            var request = api.As(user, HttpMethod.Post, path);
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            request.Content.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=" + boundary);
            return api.Client.SendAsync(request);
        }
        var checkedRequests = 0;
        foreach (var role in Enum.GetValues<Role>().Where(r => !allowed.Contains(r)))
        {
            var user = await api.SeedAsync(role);
            foreach (var body in new[] { valid, broken })
            {
                var response = await Send(user, body);
                Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{role}: {await response.Content.ReadAsStringAsync()}");
                checkedRequests++;
            }
        }
        await Http.AssertErrorAsync(await Send(await api.SeedAsync(Role.OPS), broken), 400, "BAD_REQUEST");
        Assert.Equal(2 * (Enum.GetValues<Role>().Length - allowed.Count), checkedRequests);
    }

    [Fact]
    public void LitresYearsAndNamesAreReadAsTheJavaCodeReadThem()
    {
        Assert.Equal(120_500, FleetImports.Litres("120.5"));
        Assert.Equal(80_000, FleetImports.Litres("٨٠"));
        Assert.Equal(1_250, FleetImports.Litres(" 1,25 "));
        Assert.Equal(1_250, FleetImports.Litres("١٫٢٥"));
        Assert.Null(FleetImports.Litres("1.2345"));
        Assert.Null(FleetImports.Litres("1e3"));
        Assert.Null(FleetImports.Litres("١٢٣٤٥٦٧٨٩٠"));
        Assert.Equal(2022, FleetImports.Integer("2022.0"));
        Assert.Null(FleetImports.Integer("2022.5"));
        Assert.Equal(Zawed.Domain.FuelType.GAS_92, FleetImports.Fuel("بنزين 92"));
        Assert.Equal(Zawed.Domain.FuelType.DIESEL, FleetImports.Fuel("diesel"));
        Assert.Null(FleetImports.Fuel("PETROL"));
        Assert.Equal(Tier.VIP, FleetImports.TierOf("vip"));
        Assert.Equal("'=1+1", FleetImports.Field("=1+1"));
        Assert.Equal("\"a,\"\"b\"\"\"", FleetImports.Field("a,\"b\""));
        Assert.Equal("platenumber", FleetImportFile.Normalize("﻿ Plate-Number "));
    }

    // ------------------------------------------------------------------------------------------------- helpers

    private async Task<HttpResponseMessage> UploadAsync(ZawedPrincipal ops, Guid company, string name, byte[] content)
    {
        var request = api.As(ops, HttpMethod.Post, $"/api/v1/ops/companies/{company}/fleet-imports");
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new("application/octet-stream");
        form.Add(file, "file", name);
        request.Content = form;
        return await api.Client.SendAsync(request);
    }

    private Task<HttpResponseMessage> ConfirmAsync(ZawedPrincipal ops, string job, string? mode) =>
        api.PostAsync(ops, $"/api/v1/ops/fleet-imports/{job}/confirm", mode is null ? "{}" : JsonSerializer.Serialize(new { mode }));

    /// <summary>
    /// One tick of the worker. Only this class queues imports, and xUnit runs its tests one at a time, so the job a test
    /// confirmed is the only one a tick can claim (as in the Java test).
    /// </summary>
    private Task<Guid?> RunWorkerAsync(string workerId, int maxRows = 100) => Worker.RunOnceAsync(maxRows, workerId);

    private async Task DepartmentAsync(ZawedPrincipal ops, Guid company, string name) =>
        Assert.Equal(HttpStatusCode.Created, (await api.PostAsync(ops, $"/api/v1/ops/companies/{company}/departments",
            JsonSerializer.Serialize(new { name }))).StatusCode);

    private Task<long> VehiclesAsync(Guid company) =>
        api.Owner.QueryOneAsync<long>("select count(*) from vehicles where company_id = @company", new { company });

    /// <summary>A funded wallet, as the ledger's top-up would leave it (the ledger is ported in wave 2A).</summary>
    private Task FundAsync(Guid company, long piastres) => api.Owner.ExecuteAsync("""
        with account as (
          insert into ledger_accounts (id, code, kind, company_id, created_at)
          values (gen_random_uuid(), 'company:' || @company || ':wallet', 'COMPANY_WALLET', @company, now())
          on conflict (code) do update set code = excluded.code
          returning id)
        insert into company_wallets (company_id, account_id, balance_piastres, created_at, updated_at)
        select @company, id, @piastres, now(), now() from account
        on conflict (company_id) do update set balance_piastres = excluded.balance_piastres
        """, new { company, piastres });

    private static byte[] Workbook(List<List<string>> rows) =>
        Xlsx.Write([new Xlsx.Sheet("Vehicles", [.. rows.Select(r => (IReadOnlyList<string>)r)], [], false)]);

    private static byte[] ZipBomb()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.SmallestSize).Open();
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i < 70; i++)
            {
                entry.Write(zeros);
            }
        }
        return output.ToArray();
    }

    private static byte[] ZipWith(string name, byte[] content) => ZipOf(new() { [name] = content });

    private static byte[] ZipOf(Dictionary<string, object> parts)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in parts)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(content is string text ? Encoding.UTF8.GetBytes(text) : (byte[])content);
            }
        }
        return output.ToArray();
    }
}

/// <summary>
/// The fleet import worker is off in every application the tests start (the Java test switched it off with
/// <c>zawed.fleet-import.worker-enabled=false</c>): the shared database holds every test class's jobs, and a background
/// worker in one application would take another test's job. The tests that need it drive <see cref="FleetImportWorker.RunOnceAsync"/>.
/// </summary>
internal static class WorkerOff
{
#pragma warning disable CA2255 // a test assembly's own process setting, before any application starts
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void SwitchOff() => Environment.SetEnvironmentVariable("Zawed__FleetImport__WorkerEnabled", "false");
}
