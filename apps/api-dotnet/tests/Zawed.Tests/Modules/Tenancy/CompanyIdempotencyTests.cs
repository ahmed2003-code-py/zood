using System.Net;
using Zawed.Api.Modules.Tenancy;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Support;

namespace Zawed.Tests.Modules.Tenancy;

/// <summary>
/// Onboarding a company is idempotent with an Idempotency-Key (V13, ADR 0022): a retry, a double click or racing
/// requests give one company; the same key with another body is refused (Java <c>CompanyIdempotencyTests</c>).
/// The Java race test also counts one wallet: the wallet is opened by the ledger's <c>CompanyCreated</c> handler,
/// so that half of the assertion moves to the ledger port (wave 2A).
/// </summary>
public sealed class CompanyIdempotencyTests(ProbeApi api) : IClassFixture<ProbeApi>
{
    [Fact]
    public async Task ARetryWithTheSameKeyAnswersTheSameCompany()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var key = "co-" + Guid.CreateVersion7();
        var legal = "Idem " + Guid.CreateVersion7();
        var first = await CreateAsync(ops, key, legal);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var retry = await CreateAsync(ops, key, legal);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal((await first.JsonAsync()).Text("id"), (await retry.JsonAsync()).Text("id"));
        Assert.Equal(1L, await CountAsync(legal));
        Assert.Equal(1L, await api.Owner.QueryOneAsync<long>(
            "select count(*) from audit_log where action = 'COMPANY_CREATED' and entity_id = @id::uuid", new { id = (await first.JsonAsync()).Text("id") }));

        await Http.AssertErrorAsync(await CreateAsync(ops, key, legal + " B"), 409, "IDEMPOTENCY_KEY_REUSED");
    }

    [Fact]
    public async Task RacingRequestsWithOneKeyCreateOneCompany()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var key = "co-" + Guid.CreateVersion7();
        var legal = "Race " + Guid.CreateVersion7();
        var statuses = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ => (int)(await CreateAsync(ops, key, legal)).StatusCode));
        Assert.Single(statuses, s => s == 201);
        Assert.All(statuses, s => Assert.True(s is 201 or 200, $"status {s}"));
        Assert.Equal(1L, await CountAsync(legal));
    }

    [Fact]
    public async Task AnotherUsersKeyIsNeitherSeenNorReused()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var key = "co-" + Guid.CreateVersion7();
        var legal = "Own " + Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(ops, key, legal)).StatusCode);
        var colleague = await api.SeedAsync(Role.OPS);
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(colleague, key, legal + " other")).StatusCode); // keys are per user
    }

    [Fact]
    public async Task ARefusedCreateFreesItsKey()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var key = "co-" + Guid.CreateVersion7();
        var taxId = MasterDataApi.TaxDigits();
        await api.Owner.ExecuteAsync("""
            insert into companies (id, legal_name, name_ar, name_en, tax_id, status, cooldown_minutes, low_balance_threshold_piastres, created_at, updated_at)
            values (@id, 'Holder', 'ش', 'Holder', @taxId, 'ACTIVE', 180, 0, now(), now())
            """, new { id = Guid.CreateVersion7(), taxId });
        var body = $$"""{"company":{"legalName":"Second","nameAr":"ش","nameEn":"Second","taxId":"{{taxId}}"},"lowBalanceThresholdPiastres":0}""";
        await Http.AssertErrorAsync(await api.PostAsync(ops, "/api/v1/ops/companies", body, key), 409, "TAX_ID_IN_USE");
        Assert.Equal(0L, await api.Owner.QueryOneAsync<long>("select count(*) from idempotency_keys where key = @key", new { key }));
    }

    /// <summary>
    /// A key the Java API stored must replay on the .NET API after the switch, so the body hash is the Java one:
    /// SHA-256 of Jackson 3's JSON of <c>CreateCompanyRequest</c> (record order, nulls written, non-ASCII and
    /// HTML characters unescaped). The value was computed with jackson-databind 3.1.7's default <c>JsonMapper</c>.
    /// </summary>
    [Fact]
    public void TheBodyHashIsTheJavaOne()
    {
        var request = new CreateCompanyRequest(
            new CompanyBody("Nile <Co> & \"Sons\"", "شركة النيل", "Nile Co", "123-456-789", null, null, "+20 2 2345 6789", "Ops@Nile.test"),
            null, 500000L);
        Assert.Equal("4635ab5ab2e9cda108f2d607f371c69d895ef3fb79f03b3562141584fa31e48d", IdempotencyKeys.Hash(request));
    }

    private Task<long> CountAsync(string legal) =>
        api.Owner.QueryOneAsync<long>("select count(*) from companies where legal_name = @legal", new { legal });

    private Task<HttpResponseMessage> CreateAsync(ZawedPrincipal user, string key, string legal) =>
        api.PostAsync(user, "/api/v1/ops/companies",
            $$"""{"company":{"legalName":"{{legal}}","nameAr":"شركة","nameEn":"Company"},"lowBalanceThresholdPiastres":0}""", key);
}
