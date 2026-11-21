using System.Net;
using System.Text.Json;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Modules.Tenancy;
using Zawed.Tests.Support;

namespace Zawed.Tests.Modules.Leads;

/// <summary>
/// The Ops side of the leads (u1-leads-contract.md §4; Java <c>LeadsOpsTests</c>): the list, newest first with keyset
/// paging on (created_at, id) and a status filter, and the update, audited in the same transaction with the reference and
/// statuses only.
/// </summary>
public sealed class LeadsOpsTests(ProbeApi api) : IClassFixture<ProbeApi>
{
    [Fact]
    public async Task TheListIsNewestFirstAndPagesOnCreatedAtThenId()
    {
        var ops = await api.SeedAsync(Role.OPS);
        // Far in the future, so nothing another test stores comes before them. Two share a creation time: the id decides.
        var @base = DateTimeOffset.Parse("2199-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var planted = new List<(Guid Id, DateTimeOffset At)>();
        for (var i = 0; i < 5; i++)
        {
            var at = @base.AddSeconds(i == 4 ? 3 : i);
            planted.Add((await PlantAsync(api.Owner, at, "NEW"), at));
        }
        // PostgreSQL orders uuids byte by byte, which is the order of their text.
        var expected = planted.OrderByDescending(p => p.At).ThenByDescending(p => p.Id.ToString(), StringComparer.Ordinal)
            .Select(p => p.Id.ToString()).ToList();

        var seen = new List<string?>();
        string? cursor = null;
        for (var page = 0; page < 3; page++)
        {
            var response = await api.GetAsync(ops, "/api/v1/ops/leads?limit=2" + (cursor is null ? "" : "&cursor=" + cursor));
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var body = await response.JsonAsync();
            seen.AddRange(body.Items("id"));
            cursor = body.Text("nextCursor");
            if (page < 2)
            {
                Assert.NotNull(cursor);
            }
        }
        Assert.Equal(expected, seen.Take(5));
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public async Task TheListFiltersByStatusAndShowsTheWholeLead()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var qualified = await PlantAsync(api.Owner, DateTimeOffset.Parse("2198-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), "QUALIFIED");
        await PlantAsync(api.Owner, DateTimeOffset.Parse("2198-01-01T00:00:01Z", System.Globalization.CultureInfo.InvariantCulture), "NEW");

        var response = await api.GetAsync(ops, "/api/v1/ops/leads?status=QUALIFIED");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        var statuses = body.Items("status");
        Assert.NotEmpty(statuses);
        Assert.All(statuses, s => Assert.Equal("QUALIFIED", s));
        var first = body.GetProperty("items")[0];
        Assert.Equal(["id", "reference", "companyName", "contactName", "phone", "email", "fleetSize", "locale", "status", "note", "createdAt",
            "updatedAt", "requestCount", "lastRequestedAt", "differingRequests"], first.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, first.GetProperty("requestCount").GetInt32());
        Assert.Equal(0, first.GetProperty("differingRequests").GetInt32());
        Assert.Equal(qualified.ToString(), first.Text("id"));
        Assert.Matches("^L-[0-9]{5,}$", first.Text("reference"));
        Assert.Equal("50-99", first.Text("fleetSize"));
        Assert.Equal("en", first.Text("locale"));

        // limit: the bounds of every Ops list (1–100, 25 by default).
        Assert.Equal([qualified.ToString()], (await (await api.GetAsync(ops, "/api/v1/ops/leads?status=QUALIFIED&limit=1")).JsonAsync()).Items("id"));
        var clamped = await api.GetAsync(ops, "/api/v1/ops/leads?limit=0");
        Assert.Equal(HttpStatusCode.OK, clamped.StatusCode);
        Assert.Equal(1, (await clamped.JsonAsync()).GetProperty("items").GetArrayLength());
        Assert.True((await (await api.GetAsync(ops, "/api/v1/ops/leads?limit=500")).JsonAsync()).GetProperty("items").GetArrayLength() <= 100);

        Assert.Equal(HttpStatusCode.BadRequest, (await api.GetAsync(ops, "/api/v1/ops/leads?status=WON")).StatusCode);
        await Http.AssertErrorAsync(await api.GetAsync(ops, "/api/v1/ops/leads?cursor=abc"), 400, "INVALID_CURSOR");
    }

    [Fact]
    public async Task AnUpdateChangesTheStatusAndNoteAndIsAuditedWithoutPersonalData()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var id = await PlantAsync(api.Owner, DateTimeOffset.UtcNow.AddSeconds(-60), "NEW");
        const string note = "Called.\nCall back on Sunday about pricing.";

        var contacted = await PatchAsync(ops, id, JsonSerializer.Serialize(new { status = "CONTACTED", note }));
        Assert.True(contacted.StatusCode == HttpStatusCode.OK, await contacted.Content.ReadAsStringAsync());
        var body = await contacted.JsonAsync();
        Assert.Equal("CONTACTED", body.Text("status"));
        Assert.Equal(note, body.Text("note"));
        Assert.True(DateTimeOffset.Parse(body.Text("updatedAt")!, System.Globalization.CultureInfo.InvariantCulture)
            > DateTimeOffset.Parse(body.Text("createdAt")!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(ops.UserId, await api.Owner.QueryOneAsync<Guid>("select updated_by from leads where id = @id", new { id }));

        var metadata = await api.Owner.QueryOneAsync<string>("""
            select metadata::text from audit_log
            where action = 'LEAD_UPDATED' and entity_type = 'LEAD' and entity_id = @id and actor_id = @actor and scope_type = 'PLATFORM'
            """, new { id, actor = ops.UserId });
        var reference = await api.Owner.QueryOneAsync<string>("select reference from leads where id = @id", new { id });
        using (var json = JsonDocument.Parse(metadata!))
        {
            Assert.Equal(new Dictionary<string, string> { ["reference"] = reference!, ["from"] = "NEW", ["to"] = "CONTACTED", ["noteChanged"] = "True" },
                json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.True ? "True" : p.Value.GetString()!));
        }
        foreach (var personal in new[] { "company_name", "contact_name", "phone", "email" })
        {
            Assert.DoesNotContain((await api.Owner.QueryOneAsync<string>($"select {personal} from leads where id = @id", new { id }))!, metadata);
        }
        Assert.DoesNotContain("Sunday", metadata);

        // Closing without a note keeps the note and starts the retention clock.
        var closed = await PatchAsync(ops, id, """{"status":"CLOSED"}""");
        Assert.Equal(note, (await closed.JsonAsync()).Text("note"));
        Assert.NotNull(await api.Owner.QueryOneAsync<DateTime?>("select closed_at from leads where id = @id", new { id }));

        // Reopening with a blank note removes it and stops the clock.
        var reopened = await PatchAsync(ops, id, """{"status":"NEW","note":"  "}""");
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        Assert.Null((await reopened.JsonAsync()).Text("note"));
        Assert.Null(await api.Owner.QueryOneAsync<DateTime?>("select closed_at from leads where id = @id", new { id }));
        // The web clears a note with an empty string: stored as null.
        await PatchAsync(ops, id, """{"status":"NEW","note":"again"}""");
        var cleared = await PatchAsync(ops, id, """{"status":"NEW","note":""}""");
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Null((await cleared.JsonAsync()).Text("note"));
        Assert.Null(await api.Owner.QueryOneAsync<string>("select note from leads where id = @id", new { id }));
        Assert.Equal(5L, await api.Owner.QueryOneAsync<long>("select count(*) from audit_log where action = 'LEAD_UPDATED' and entity_id = @id", new { id }));
    }

    [Fact]
    public async Task AnInvalidUpdateIsRefusedAndAnUnknownLeadIs404()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var id = await PlantAsync(api.Owner, DateTimeOffset.UtcNow.AddSeconds(-60), "NEW");
        Assert.Equal("SIZE", await FieldCodeAsync(await PatchAsync(ops, id, JsonSerializer.Serialize(new { status = "CONTACTED", note = new string('x', 1001) })), "note"));
        Assert.Equal("PATTERN", await FieldCodeAsync(await PatchAsync(ops, id, """{"status":"CONTACTED","note":"bell\u0007"}"""), "note"));
        Assert.Equal("NOT_NULL", await FieldCodeAsync(await PatchAsync(ops, id, """{"note":"x"}"""), "status"));
        Assert.Equal(HttpStatusCode.BadRequest, (await PatchAsync(ops, id, """{"status":"WON"}""")).StatusCode);
        Assert.Equal("NEW", await api.Owner.QueryOneAsync<string>("select status from leads where id = @id", new { id }));

        await Http.AssertErrorAsync(await PatchAsync(ops, Guid.CreateVersion7(), """{"status":"CONTACTED"}"""), 404, "NOT_FOUND");
        // Ops only: a company user is refused before anything is read.
        var manager = await api.SeedAsync(Role.FLEET_MANAGER);
        await Http.AssertErrorAsync(await api.GetAsync(manager, "/api/v1/ops/leads"), 403, "FORBIDDEN");
    }

    /// <summary>A lead written as the owner, with its creation time chosen by the test (Java <c>LeadsOpsTests.plant</c>).</summary>
    internal static async Task<Guid> PlantAsync(Owner owner, DateTimeOffset createdAt, string status)
    {
        var id = Guid.CreateVersion7(createdAt);
        var token = Guid.NewGuid().ToString()[..8];
        await owner.ExecuteAsync("""
            insert into leads (id, reference, company_name, contact_name, phone, email, fleet_size, locale, status,
                               request_count, last_requested_at, created_at, updated_at, closed_at)
            values (@id, 'L-' || nextval('lead_reference_seq'), @company, @contact, @phone, @email, '50-99', 'en', @status, 1, @at, @at, @at,
                    case when @status = 'CLOSED' then cast(@at as timestamptz) end)
            """, new
        {
            id, company = "Planted Co " + token, contact = "Planted " + token, phone = "010" + Random.Shared.Next(100_000_000).ToString("D8", System.Globalization.CultureInfo.InvariantCulture),
            email = "p-" + token + "@example.test", status, at = createdAt.UtcDateTime,
        });
        return id;
    }

    private Task<HttpResponseMessage> PatchAsync(ZawedPrincipal ops, Guid id, string body) => api.SendAsync(ops, HttpMethod.Patch, $"/api/v1/ops/leads/{id}", body);

    private static async Task<string?> FieldCodeAsync(HttpResponseMessage response, string field)
    {
        var body = await Http.AssertErrorAsync(response, 400, "VALIDATION_FAILED");
        return body.GetProperty("fields").EnumerateArray().Where(f => f.Text("field") == field).Select(f => f.Text("code")).FirstOrDefault();
    }
}
