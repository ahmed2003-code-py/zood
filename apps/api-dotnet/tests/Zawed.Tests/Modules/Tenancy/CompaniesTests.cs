using System.Net;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Support;

namespace Zawed.Tests.Modules.Tenancy;

/// <summary>Company onboarding by Ops and the company's read-only view of itself (Java <c>CompaniesTests</c>).</summary>
public sealed class CompaniesTests(ProbeApi api) : IClassFixture<ProbeApi>
{
    [Fact]
    public async Task OpsOnboardsACompanyWithTheDefaultCooldown()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var taxId = MasterDataApi.TaxDigits();
        var created = await api.PostAsync(ops, "/api/v1/ops/companies", $$"""
            {"company":{"legalName":"Nile Logistics S.A.E.","nameAr":"النيل للخدمات اللوجستية","nameEn":"Nile Logistics",
            "taxId":"{{taxId}}","contactEmail":"Ops@Nile.test"},"lowBalanceThresholdPiastres":500000}
            """);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.JsonAsync();
        Assert.Equal(180, body.GetProperty("cooldownMinutes").GetInt32());
        Assert.Equal("ops@nile.test", body.Text("contactEmail"));
        Assert.Equal("ACTIVE", body.Text("status"));
        Assert.Equal(500000, body.GetProperty("lowBalanceThresholdPiastres").GetInt64());

        var duplicate = await api.PostAsync(ops, "/api/v1/ops/companies", $$"""
            {"company":{"legalName":"Other","nameAr":"أخرى","nameEn":"Other","taxId":"{{taxId}}"},"lowBalanceThresholdPiastres":0}
            """);
        var refused = await Http.AssertErrorAsync(duplicate, 409, "TAX_ID_IN_USE");
        Assert.Equal("taxId", refused.Text("field"));
    }

    [Fact]
    public async Task OnlyASuperAdminChangesTheCooldownAndTheChangeIsAudited()
    {
        var company = await api.SeedCompanyAsync();
        const string body = """{"cooldownMinutes":240,"lowBalanceThresholdPiastres":100000,"reason":"Contract"}""";
        await Http.AssertErrorAsync(await api.PutAsync(await api.SeedAsync(Role.OPS), $"/api/v1/ops/companies/{company}/policy", body),
            403, "FORBIDDEN");

        var changed = await api.PutAsync(await api.SeedAsync(Role.SUPER_ADMIN), $"/api/v1/ops/companies/{company}/policy", body);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(240, (await changed.JsonAsync()).GetProperty("cooldownMinutes").GetInt32());
        var metadata = await api.Owner.QueryOneAsync<string>(
            "select metadata::text from audit_log where action = 'COMPANY_POLICY_CHANGED' and entity_id = @company", new { company });
        Assert.Contains("\"from\": 180", metadata);
        Assert.Contains("\"to\": 240", metadata);
        Assert.Contains("Contract", metadata);

        var outOfRange = await api.PutAsync(await api.SeedAsync(Role.SUPER_ADMIN), $"/api/v1/ops/companies/{company}/policy",
            """{"cooldownMinutes":60,"lowBalanceThresholdPiastres":0,"reason":"x"}""");
        await MasterDataApi.AssertInvalidAsync(outOfRange, "cooldownMinutes");
    }

    [Fact]
    public async Task ACompanyReadsOnlyItself()
    {
        var mine = await api.SeedCompanyAsync();
        var theirs = await api.SeedCompanyAsync();
        var ops = await api.SeedAsync(Role.OPS);
        Assert.Equal(HttpStatusCode.Created, (await api.PostAsync(ops, $"/api/v1/ops/companies/{mine}/departments", """{"name":"Sales"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await api.PostAsync(ops, $"/api/v1/ops/companies/{theirs}/departments", """{"name":"Secret"}""")).StatusCode);

        var manager = await api.SeedAsync(Role.FLEET_MANAGER, Scope.Company(mine));
        var profile = await api.GetAsync(manager, "/api/v1/company/profile");
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        Assert.Equal(mine.ToString(), (await profile.JsonAsync()).Text("id"));
        Assert.DoesNotContain("taxId", await profile.Content.ReadAsStringAsync());
        var departments = await (await api.GetAsync(manager, "/api/v1/company/departments")).JsonAsync();
        Assert.Equal(["Sales"], departments.Items("name"));
        // The company portal has no write endpoints, and the Ops ones are closed to it.
        await Http.AssertErrorAsync(await api.GetAsync(manager, $"/api/v1/ops/companies/{mine}"), 403, "FORBIDDEN");
    }

    [Fact]
    public async Task ADepartmentNameIsUniqueWithinACompany()
    {
        var company = await api.SeedCompanyAsync();
        var ops = await api.SeedAsync(Role.OPS);
        Assert.Equal(HttpStatusCode.Created,
            (await api.PostAsync(ops, $"/api/v1/ops/companies/{company}/departments", """{"name":"Field Team"}""")).StatusCode);
        var again = await api.PostAsync(ops, $"/api/v1/ops/companies/{company}/departments", """{"name":"field  team"}""");
        var refused = await Http.AssertErrorAsync(again, 409, "DEPARTMENT_EXISTS");
        Assert.Equal("name", refused.Text("field"));
        // Another company may use the name.
        Assert.Equal(HttpStatusCode.Created, (await api.PostAsync(ops, $"/api/v1/ops/companies/{await api.SeedCompanyAsync()}/departments",
            """{"name":"Field Team"}""")).StatusCode);
    }

    // ------------------------------------------------------------------------------------------------------
    // Beyond the Java class: the rest of the B2 contract for companies, pinned down here because the .NET
    // module is the reference for every EF Core module (statuses, codes, audit rows, pagination).

    [Fact]
    public async Task OpsEditsAFreezesAndListsCompaniesAndEveryChangeIsAudited()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var name = "Edit " + Guid.CreateVersion7().ToString()[..8];
        var created = await (await api.PostAsync(ops, "/api/v1/ops/companies", $$"""
            {"company":{"legalName":"{{name}}","nameAr":"شركة","nameEn":"{{name}}"},"cooldownMinutes":150,"lowBalanceThresholdPiastres":0}
            """)).JsonAsync();
        var id = created.Text("id");
        Assert.Equal(150, created.GetProperty("cooldownMinutes").GetInt32());
        Assert.Equal(1L, await api.Owner.QueryOneAsync<long>(
            "select count(*) from audit_log where action = 'COMPANY_CREATED' and entity_id = @id::uuid and scope_type = 'COMPANY' and scope_id = @id::uuid",
            new { id }));

        var edited = await api.PutAsync(ops, $"/api/v1/ops/companies/{id}", $$"""
            {"legalName":"  {{name}}   Renamed ","nameAr":"شركة","nameEn":"{{name}}","contactName":"Mona","contactEmail":"MONA@Nile.Test"}
            """);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var after = await edited.JsonAsync();
        Assert.Equal($"{name} Renamed", after.Text("legalName"));
        Assert.Equal("mona@nile.test", after.Text("contactEmail"));
        Assert.Contains(name, await api.Owner.QueryOneAsync<string>(
            "select metadata->>'legalNameBefore' from audit_log where action = 'COMPANY_UPDATED' and entity_id = @id::uuid", new { id }));

        var frozen = await api.PostAsync(ops, $"/api/v1/ops/companies/{id}/status", """{"status":"FROZEN","reason":" Unpaid "}""");
        Assert.Equal("FROZEN", (await frozen.JsonAsync()).Text("status"));
        Assert.Equal("Unpaid", await api.Owner.QueryOneAsync<string>(
            "select metadata->>'reason' from audit_log where action = 'COMPANY_FROZEN' and entity_id = @id::uuid", new { id }));
        // Freezing a frozen company changes nothing and records nothing.
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync(ops, $"/api/v1/ops/companies/{id}/status", """{"status":"FROZEN","reason":"again"}""")).StatusCode);
        Assert.Equal(1L, await api.Owner.QueryOneAsync<long>(
            "select count(*) from audit_log where action = 'COMPANY_FROZEN' and entity_id = @id::uuid", new { id }));

        var listed = await (await api.GetAsync(ops, $"/api/v1/ops/companies?status=FROZEN&q={Uri.EscapeDataString(name.ToUpperInvariant())}")).JsonAsync();
        Assert.Equal([id], listed.Items("id"));
        Assert.Empty((await (await api.GetAsync(ops, $"/api/v1/ops/companies?status=ACTIVE&q={name}")).JsonAsync()).Items("id"));

        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync(ops, $"/api/v1/ops/companies/{id}/status", """{"status":"ACTIVE","reason":"Paid"}""")).StatusCode);
        Assert.Equal(1L, await api.Owner.QueryOneAsync<long>(
            "select count(*) from audit_log where action = 'COMPANY_UNFROZEN' and entity_id = @id::uuid", new { id }));
    }

    [Fact]
    public async Task ASearchTreatsLikeWildcardsAsText()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var marker = Guid.CreateVersion7().ToString("N")[..10];
        var created = await (await api.PostAsync(ops, "/api/v1/ops/companies", $$"""
            {"company":{"legalName":"100% {{marker}}_x","nameAr":"شركة","nameEn":"Pct"},"lowBalanceThresholdPiastres":0}
            """)).JsonAsync();
        Assert.Equal([created.Text("id")],
            (await (await api.GetAsync(ops, $"/api/v1/ops/companies?q={Uri.EscapeDataString($"0% {marker}_")}")).JsonAsync()).Items("id"));
        Assert.Empty((await (await api.GetAsync(ops, $"/api/v1/ops/companies?q={Uri.EscapeDataString($"1_0% {marker}")}")).JsonAsync()).Items("id"));
    }

    [Fact]
    public async Task ListsArePagedNewestFirstWithACursor()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var company = await api.SeedCompanyAsync();
        foreach (var name in new[] { "One", "Two", "Three" })
        {
            Assert.Equal(HttpStatusCode.Created, (await api.PostAsync(ops, $"/api/v1/ops/companies/{company}/departments",
                $$"""{"name":"{{name}}","monthlyBudgetPiastres":1000}""")).StatusCode);
        }
        var first = await (await api.GetAsync(ops, $"/api/v1/ops/companies/{company}/departments?limit=2")).JsonAsync();
        Assert.Equal(["Three", "Two"], first.Items("name"));
        Assert.True(first.GetProperty("items")[0].GetProperty("active").GetBoolean());
        Assert.Equal(1000, first.GetProperty("items")[0].GetProperty("monthlyBudgetPiastres").GetInt64());
        var cursor = first.Text("nextCursor");
        var second = await (await api.GetAsync(ops, $"/api/v1/ops/companies/{company}/departments?limit=2&cursor={cursor}")).JsonAsync();
        Assert.Equal(["One"], second.Items("name"));
        Assert.Null(second.Text("nextCursor"));
        await Http.AssertErrorAsync(await api.GetAsync(ops, $"/api/v1/ops/companies/{company}/departments?cursor=nope"), 400, "INVALID_CURSOR");
    }

    [Fact]
    public async Task AnUnknownCompanyIsNotFoundEverywhere()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var unknown = Guid.CreateVersion7();
        await Http.AssertErrorAsync(await api.GetAsync(ops, $"/api/v1/ops/companies/{unknown}"), 404, "NOT_FOUND");
        await Http.AssertErrorAsync(await api.GetAsync(ops, $"/api/v1/ops/companies/{unknown}/departments"), 404, "NOT_FOUND");
        await Http.AssertErrorAsync(await api.PostAsync(ops, $"/api/v1/ops/companies/{unknown}/departments", """{"name":"X"}"""), 404, "NOT_FOUND");
        await Http.AssertErrorAsync(await api.PutAsync(ops, $"/api/v1/ops/companies/{unknown}",
            """{"legalName":"X","nameAr":"X","nameEn":"X"}"""), 404, "NOT_FOUND");
        await Http.AssertErrorAsync(await api.PostAsync(ops, $"/api/v1/ops/companies/{unknown}/status",
            """{"status":"FROZEN","reason":"x"}"""), 404, "NOT_FOUND");
        await Http.AssertErrorAsync(await api.PutAsync(await api.SeedAsync(Role.SUPER_ADMIN), $"/api/v1/ops/companies/{unknown}/policy",
            """{"cooldownMinutes":120,"lowBalanceThresholdPiastres":0,"reason":"x"}"""), 404, "NOT_FOUND");
    }

    [Fact]
    public async Task ARequestBodyIsValidatedWithTheJakartaCodes()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var invalid = await Http.AssertErrorAsync(await api.PostAsync(ops, "/api/v1/ops/companies",
            """{"company":{"legalName":" ","nameAr":"ش","nameEn":"N","contactEmail":"not-an-email"},"cooldownMinutes":300}"""),
            400, "VALIDATION_FAILED");
        var fields = invalid.GetProperty("fields").EnumerateArray()
            .Select(f => $"{f.GetProperty("field").GetString()}:{f.GetProperty("code").GetString()}").Order().ToList();
        Assert.Equal(["company.contactEmail:EMAIL", "company.legalName:NOT_BLANK", "cooldownMinutes:MAX", "lowBalanceThresholdPiastres:NOT_NULL"], fields);

        await MasterDataApi.AssertInvalidAsync(await api.PostAsync(ops, $"/api/v1/ops/companies/{await api.SeedCompanyAsync()}/status",
            """{"status":"FROZEN","reason":"  "}"""), "reason");
        await MasterDataApi.AssertInvalidAsync(await api.PostAsync(ops, $"/api/v1/ops/companies/{await api.SeedCompanyAsync()}/departments",
            """{"name":"D","monthlyBudgetPiastres":-1}"""), "monthlyBudgetPiastres");
    }

    [Theory]
    [InlineData("/api/v1/ops/companies/not-a-uuid")]
    [InlineData("/api/v1/ops/companies?status=GONE")]
    [InlineData("/api/v1/ops/companies?limit=ten")]
    public async Task AnUnreadablePathOrQueryValueIsABadRequest(string path)
    {
        // Spring answers a type mismatch with the generic 400, nothing from the request echoed.
        var body = await Http.AssertErrorAsync(await api.GetAsync(await api.SeedAsync(Role.OPS), path), 400, "BAD_REQUEST");
        Assert.Equal("Bad Request", body.Text("message"));
        Assert.False(body.TryGetProperty("fields", out _));
    }
}
