using Zawed.Tests.Modules.Tenancy;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Network.PosTestSupport;

namespace Zawed.Tests.Modules.Network;

/// <summary>
/// The operator halves of the Java <c>CompanyContactTests</c>, moved here from wave 1C with <c>/ops/operators</c>: an
/// operator's contact phone, tax registration number and commercial registration are cleaned and judged as a company's.
/// </summary>
public sealed class OperatorContactTests(SharedApi api) : IClassFixture<SharedApi>
{
    private readonly PosTestSupport pos = new(api.Factory);

    private async Task<HttpResponseMessage> OpsAsync(string method, string path, string json) =>
        await pos.Id.AuthedAsync(method, path, await pos.Ops.Value, json);

    [Fact]
    public async Task AnOperatorsContactDetailsAreNormalisedToo()
    {
        var digits = MasterDataApi.TaxDigits();
        var created = await Json(await OpsAsync("POST", "/api/v1/ops/operators",
            $$"""{"legalName":"Op {{Guid.CreateVersion7()}}","nameAr":"م","nameEn":"Op","taxId":"{{digits[..3]}}-{{digits[3..6]}}-{{digits[6..]}}","contactPhone":"+20 3 4567890","contactEmail":"Ops@Station.TEST"}"""), 201);
        Assert.Equal("034567890", created["contactPhone"]!.GetValue<string>());
        Assert.Equal(digits, created["taxId"]!.GetValue<string>());
        Assert.Equal("ops@station.test", created["contactEmail"]!.GetValue<string>());
    }

    [Fact]
    public async Task InvalidContactDetailsAreRefusedByField()
    {
        await MasterDataApi.AssertInvalidAsync(await OpsAsync("POST", "/api/v1/ops/operators",
            """{"legalName":"Op","nameAr":"م","nameEn":"Op","contactPhone":"5551234"}"""), "contactPhone");
        await MasterDataApi.AssertInvalidAsync(await OpsAsync("POST", "/api/v1/ops/operators",
            """{"legalName":"Op","nameAr":"م","nameEn":"Op","taxId":"x"}"""), "taxId");
    }

    [Fact]
    public async Task ALegacySpellingOfATaxIdStillBlocksTheSameNumber()
    {
        var digits = MasterDataApi.TaxDigits();
        var legacy = Guid.CreateVersion7();
        await pos.Owner.ExecuteAsync("""
            insert into station_operators (id, legal_name, name_ar, name_en, tax_id, status, created_at, updated_at)
            values (@legacy, 'Legacy Op', 'م', 'Legacy Op', @tax, 'ACTIVE', now(), now())
            """, new { legacy, tax = digits[..3] + " " + digits[3..] });
        await Http.AssertErrorAsync(await OpsAsync("POST", "/api/v1/ops/operators",
            $$"""{"legalName":"Op","nameAr":"م","nameEn":"Op","taxId":"{{digits}}"}"""), 409, "TAX_ID_IN_USE");
        // Ops finds it by the tax id however it is typed.
        var found = await Json(await pos.Id.AuthedAsync("GET", $"/api/v1/ops/operators?q={digits[..3]}-{digits[3..]}", await pos.Ops.Value), 200);
        Assert.Equal([legacy.ToString()], found["items"]!.AsArray().Select(o => o!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task AnUnchangedLegacyValueIsKeptOnAFullBodyUpdate()
    {
        var legacy = Guid.CreateVersion7();
        await pos.Owner.ExecuteAsync("""
            insert into station_operators (id, legal_name, name_ar, name_en, contact_phone, commercial_registration, status, created_at, updated_at)
            values (@legacy, 'Legacy Op', 'م', 'Legacy Op', 'call the office', 'CR old/7', 'ACTIVE', now(), now())
            """, new { legacy });
        var updated = await Json(await OpsAsync("PUT", $"/api/v1/ops/operators/{legacy}",
            """{"legalName":"Legacy Op 2","nameAr":"م","nameEn":"Legacy Op","contactPhone":"call the office","commercialRegistration":"CR old/7"}"""), 200);
        Assert.Equal("call the office", updated["contactPhone"]!.GetValue<string>());
        Assert.Equal("Legacy Op 2", updated["legalName"]!.GetValue<string>());
        await MasterDataApi.AssertInvalidAsync(await OpsAsync("PUT", $"/api/v1/ops/operators/{legacy}",
            """{"legalName":"Legacy Op 2","nameAr":"م","nameEn":"Legacy Op","contactPhone":"call me later","commercialRegistration":"CR old/7"}"""), "contactPhone");
    }
}
