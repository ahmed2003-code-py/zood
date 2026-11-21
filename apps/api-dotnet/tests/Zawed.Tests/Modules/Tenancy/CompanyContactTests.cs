using System.Net;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Support;

namespace Zawed.Tests.Modules.Tenancy;

/// <summary>
/// A company's contact phone, tax registration number and commercial registration are validated and stored in
/// one form (QA round 1): <c>+20 2 2345 6789</c> is stored as <c>0223456789</c> and <c>123-456-789</c> as
/// <c>123456789</c>. Rows written before the rule stay readable as they are (Java <c>CompanyContactTests</c>).
/// The operator halves of the Java class (<c>anOperatorsContactDetailsAreNormalisedToo</c> and the operator
/// lines of two others) move with the network module (wave 2B), which serves <c>/ops/operators</c>.
/// </summary>
public sealed class CompanyContactTests(ProbeApi api) : IClassFixture<ProbeApi>
{
    [Fact]
    public async Task ContactDetailsAreNormalisedOnCreateAndUpdate()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var digits = MasterDataApi.TaxDigits();
        var typedTaxId = $"{digits[..3]}-{digits[3..6]}-{digits[6..]}";
        var created = await CreateCompanyAsync(ops, $$"""
            "taxId":"{{typedTaxId}}","commercialRegistration":"١٢٣٤٥","contactPhone":"+20 2 2345 6789"
            """);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.JsonAsync();
        Assert.Equal(digits, body.Text("taxId"));
        Assert.Equal("12345", body.Text("commercialRegistration"));
        Assert.Equal("0223456789", body.Text("contactPhone"));
        var id = body.Text("id");

        var updated = await api.PutAsync(ops, $"/api/v1/ops/companies/{id}", $$"""
            {"legalName":"Contact Co","nameAr":"شركة","nameEn":"Contact Co","taxId":"{{digits}}",
            "contactPhone":"0020 100 123 4567","commercialRegistration":"  "}
            """);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var after = await updated.JsonAsync();
        Assert.Equal("01001234567", after.Text("contactPhone"));
        Assert.Null(after.Text("commercialRegistration")); // blank is no value

        // The same tax id typed with dashes is the same company.
        Assert.Equal("TAX_ID_IN_USE", await (await CreateCompanyAsync(ops, $"\"taxId\":\"{typedTaxId}\"")).CodeAsync());

        // Ops finds it by the tax id however it is typed.
        var found = await (await api.GetAsync(ops, $"/api/v1/ops/companies?q={typedTaxId}")).JsonAsync();
        Assert.Equal([id], found.Items("id"));
    }

    [Theory]
    [InlineData("\"contactPhone\":\"12345\"", "company.contactPhone")]
    [InlineData("\"contactPhone\":\"01301234567\"", "company.contactPhone")]
    [InlineData("\"contactPhone\":\"call me\"", "company.contactPhone")]
    [InlineData("\"taxId\":\"TAX-123\"", "company.taxId")]
    [InlineData("\"taxId\":\"12345678\"", "company.taxId")]
    [InlineData("\"commercialRegistration\":\"CR/ABC\"", "company.commercialRegistration")]
    public async Task InvalidContactDetailsAreRefusedByField(string fields, string field)
    {
        var ops = await api.SeedAsync(Role.OPS);
        await MasterDataApi.AssertInvalidAsync(await CreateCompanyAsync(ops, fields), field);
    }

    [Fact]
    public async Task EveryInvalidContactDetailIsNamedAtOnce()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var body = await Http.AssertErrorAsync(await CreateCompanyAsync(ops, "\"taxId\":\"x\",\"contactPhone\":\"y\",\"commercialRegistration\":\"z\""),
            400, "VALIDATION_FAILED");
        Assert.Equal(["company.taxId", "company.commercialRegistration", "company.contactPhone"],
            body.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("field").GetString()));
        Assert.All(body.GetProperty("fields").EnumerateArray(), f => Assert.Equal("PATTERN", f.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task RowsWrittenBeforeTheRuleStayReadable()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var legacy = await InsertLegacyAsync();
        var read = await api.GetAsync(ops, $"/api/v1/ops/companies/{legacy}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var body = await read.JsonAsync();
        Assert.Equal("call the office", body.Text("contactPhone"));
        Assert.Equal("CR old/7", body.Text("commercialRegistration"));
    }

    /// <summary>
    /// QA round 1 review: a row written before the rule may hold 123-456-789 or Arabic-Indic digits. The unique
    /// index compares <c>tax_id_key(tax_id)</c> since V23, so any spelling of the same number is the same tax id.
    /// </summary>
    [Fact]
    public async Task ALegacySpellingOfATaxIdStillBlocksTheSameNumber()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var digits = MasterDataApi.TaxDigits();
        var dashed = $"{digits[..3]}-{digits[3..6]}-{digits[6..]}";
        var arabicDashed = new string(dashed.Select(c => c is >= '0' and <= '9' ? (char)('٠' + (c - '0')) : c).ToArray());
        var legacy = Guid.CreateVersion7();
        await api.Owner.ExecuteAsync("""
            insert into companies (id, legal_name, name_ar, name_en, tax_id, status, cooldown_minutes,
                                   low_balance_threshold_piastres, created_at, updated_at)
            values (@legacy, 'Legacy Tax', 'قديمة', 'Legacy Tax', @arabicDashed, 'ACTIVE', 180, 0, now(), now())
            """, new { legacy, arabicDashed });

        Assert.Equal("TAX_ID_IN_USE", await (await CreateCompanyAsync(ops, $"\"taxId\":\"{digits}\"")).CodeAsync());
        foreach (var query in new[] { digits, dashed })
        {
            var found = await (await api.GetAsync(ops, $"/api/v1/ops/companies?q={query}")).JsonAsync();
            Assert.Equal([legacy.ToString()], found.Items("id"));
        }
    }

    /// <summary>
    /// A full-body PUT sends back what it read. A legacy value that was never valid is accepted while it is
    /// unchanged; only a field that changes is judged by the new rules.
    /// </summary>
    [Fact]
    public async Task AnUnchangedLegacyValueIsAcceptedOnUpdate()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var legacy = await InsertLegacyAsync();
        var legacyTax = LegacyTax(legacy);
        var unchanged = $"\"taxId\":\"{legacyTax}\",\"commercialRegistration\":\"CR old/7\",\"contactPhone\":\"call the office\"";

        var renamed = await api.PutAsync(ops, $"/api/v1/ops/companies/{legacy}",
            $"{{\"legalName\":\"Legacy Renamed\",\"nameAr\":\"قديمة\",\"nameEn\":\"Legacy\",{unchanged}}}");
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var body = await renamed.JsonAsync();
        Assert.Equal("Legacy Renamed", body.Text("legalName"));
        Assert.Equal("call the office", body.Text("contactPhone"));
        Assert.Equal(legacyTax, body.Text("taxId"));

        // A changed field is judged; the unchanged ones still pass.
        await MasterDataApi.AssertInvalidAsync(await api.PutAsync(ops, $"/api/v1/ops/companies/{legacy}", $$"""
            {"legalName":"Legacy","nameAr":"قديمة","nameEn":"Legacy","taxId":"{{legacyTax}}","commercialRegistration":"CR old/7","contactPhone":"12345"}
            """), "contactPhone");
        var fixedUp = await api.PutAsync(ops, $"/api/v1/ops/companies/{legacy}", $$"""
            {"legalName":"Legacy","nameAr":"قديمة","nameEn":"Legacy","taxId":"{{legacyTax}}","contactPhone":"+20 2 2345 6789"}
            """);
        Assert.Equal(HttpStatusCode.OK, fixedUp.StatusCode);
        var after = await fixedUp.JsonAsync();
        Assert.Equal("0223456789", after.Text("contactPhone"));
        Assert.Null(after.Text("commercialRegistration")); // omitted is cleared
    }

    /// <summary>A tax id that was never valid, unique per row (the random end of the id, not its time prefix).</summary>
    private static string LegacyTax(Guid id) => "TAX-" + id.ToString("N")[^8..];

    private async Task<Guid> InsertLegacyAsync()
    {
        var legacy = Guid.CreateVersion7();
        await api.Owner.ExecuteAsync("""
            insert into companies (id, legal_name, name_ar, name_en, tax_id, commercial_registration, contact_phone,
                                   status, cooldown_minutes, low_balance_threshold_piastres, created_at, updated_at)
            values (@legacy, 'Legacy', 'قديمة', 'Legacy', @tax, 'CR old/7', 'call the office', 'ACTIVE', 180, 0, now(), now())
            """, new { legacy, tax = LegacyTax(legacy) });
        return legacy;
    }

    private Task<HttpResponseMessage> CreateCompanyAsync(ZawedPrincipal ops, string fields) =>
        api.PostAsync(ops, "/api/v1/ops/companies",
            $"{{\"company\":{{\"legalName\":\"Contact Co\",\"nameAr\":\"شركة\",\"nameEn\":\"Contact Co\",{fields}}},\"lowBalanceThresholdPiastres\":0}}");
}
