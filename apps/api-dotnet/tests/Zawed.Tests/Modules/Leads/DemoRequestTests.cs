using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Zawed.Api.Modules.Leads;
using Zawed.Api.Platform.RateLimit;
using Zawed.Tests.Modules.Tenancy;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Leads.DemoFormApi;

namespace Zawed.Tests.Modules.Leads;

/// <summary>
/// The public demo-request form (u1-leads-contract.md; Java <c>DemoRequestTests</c>), over HTTP against the real pipeline and
/// database: validation and normalisation, the honeypot and the time trap (202 and no row), repeats, the body cap, the
/// shared per-address and per-contact limits, and no personal data in the log. Every request here comes from this class's
/// own address, and each test starts and ends with no counters for it.
/// </summary>
[Collection(DemoFormCollection.Name)]
public sealed class DemoRequestTests(DemoFormApi api, KestrelApi kestrel) : IClassFixture<DemoFormApi>, IClassFixture<KestrelApi>, IAsyncLifetime
{
    private const string Received = "{\"status\":\"RECEIVED\"}";

    public Task InitializeAsync() => api.ForgetCountersAsync();

    public Task DisposeAsync() => api.ForgetCountersAsync();

    // ------------------------------------------------------------------------------------------------ the happy path

    [Fact]
    public async Task AValidRequestBecomesANewLeadWithCleanedValues()
    {
        var digits = Digits();
        var token = Token();
        var form = Form();
        form["companyName"] = "  Nile \t Freight\n" + token + "  ";
        form["contactName"] = " Mona   Adel ";
        form["phone"] = "+20 10" + digits[..2] + "-" + digits[2..5] + " " + digits[5..];
        form["email"] = "  Mona.Adel+" + token + "@Example.TEST ";
        var created = api.Counted("created");

        var response = await api.SubmitAsync(form);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(Received, await response.Content.ReadAsStringAsync());
        var email = "mona.adel+" + token + "@example.test";
        Assert.Equal("010" + digits, await api.Owner.QueryOneAsync<string>("select phone from leads where email = @email", new { email }));
        Assert.Equal("Nile Freight " + token, await api.Owner.QueryOneAsync<string>("select company_name from leads where email = @email", new { email }));
        Assert.Equal("Mona Adel", await api.Owner.QueryOneAsync<string>("select contact_name from leads where email = @email", new { email }));
        Assert.Matches("^L-[0-9]{5,}$", await api.Owner.QueryOneAsync<string>("select reference from leads where email = @email", new { email }));
        Assert.Equal("NEW 1 20-49 ar", await api.Owner.QueryOneAsync<string>(
            "select status || ' ' || request_count || ' ' || fleet_size || ' ' || locale from leads where email = @email", new { email }));
        Assert.Equal(created + 1, api.Counted("created"));
    }

    [Fact]
    public async Task ArabicDigitsAndTheInternationalPrefixAreTheSameNumber()
    {
        var form = Form();
        var digits = Digits()[1..];
        form["phone"] = Arabic("0020 100 " + digits[..3] + " " + digits[3..]);
        var response = await api.SubmitAsync(form);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("0100" + digits, await api.Owner.QueryOneAsync<string>("select phone from leads where email = @email", new { email = form["email"] }));
    }

    // --------------------------------------------------------------------------------------------------- validation

    public static TheoryData<string, string?, string> InvalidFields => new()
    {
        { "companyName", "A", "SIZE" }, { "companyName", "Nile\u0007Freight", "PATTERN" }, { "companyName", new string('x', 121), "SIZE" },
        { "companyName", null, "NOT_BLANK" }, { "contactName", "Mona‮Adel", "PATTERN" }, { "contactName", "   ", "NOT_BLANK" },
        { "phone", "01301234567", "PATTERN" }, { "phone", "0100123456", "PATTERN" }, { "phone", "+1 202 555 0100", "PATTERN" },
        { "phone", null, "NOT_BLANK" }, { "email", "not-an-email", "EMAIL" }, { "email", "mona　x@example.com", "EMAIL" },
        { "email", "mona@exa mple.com", "EMAIL" }, { "email", "mona​@example.com", "EMAIL" },
        { "companyName", "Nile​Freight", "PATTERN" }, { "contactName", "Mona‏Adel", "PATTERN" }, { "contactName", "Mona﻿", "PATTERN" },
        { "companyName", "NileCo", "PATTERN" }, { "email", "mona@localhost", "EMAIL" }, { "email", new string('m', 250) + "@x.eg", "SIZE" },
        { "fleetSize", "10-20", "PATTERN" }, { "fleetSize", null, "NOT_NULL" }, { "locale", "fr", "PATTERN" }, { "startedAt", null, "NOT_NULL" },
    };

    [Theory, MemberData(nameof(InvalidFields))]
    public async Task InvalidFieldsAre400WithTheFieldAndAStableCodeAndStoreNothing(string field, string? value, string code)
    {
        var form = Form();
        form[field] = value;
        var response = await api.SubmitAsync(form);
        var body = await Http.AssertErrorAsync(response, 400, "VALIDATION_FAILED");
        var codes = body.GetProperty("fields").EnumerateArray().Where(f => f.Text("field") == field).Select(f => f.Text("code")).ToList();
        Assert.Contains(code, codes);
        Assert.Equal(0L, await api.Owner.QueryOneAsync<long>("select count(*) from leads where email = @email or phone = @phone",
            new { email = form["email"]?.ToString()?.ToLowerInvariant(), phone = form["phone"]?.ToString() }));
    }

    [Fact]
    public async Task AnUnknownFieldOrABrokenBodyIsRefused()
    {
        var form = Form();
        form["source"] = "campaign";
        Assert.Equal(HttpStatusCode.BadRequest, (await api.SubmitAsync(form)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync(FormPath, "{\"companyName\":")).StatusCode);
        Assert.Equal(0L, await api.Owner.QueryOneAsync<long>("select count(*) from leads where email = @email", new { email = form["email"] }));
    }

    [Fact]
    public async Task ABodyOver4KiBIsRefusedBeforeItIsParsedWithOrWithoutALength()
    {
        var form = Form();
        form["companyName"] = "Nile " + new string('x', 4200);
        var sized = await api.SubmitAsync(form);
        var refused = await Http.AssertErrorAsync(sized, 413, "PAYLOAD_TOO_LARGE");
        Assert.Equal(4096, refused.GetProperty("params").GetProperty("maxBytes").GetInt32());

        // Chunked: no Content-Length to trust; the gate stops reading after the cap.
        var content = new StreamContent(new Unseekable(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(form))));
        content.Headers.ContentType = new("application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, FormPath) { Content = content };
        var chunked = await api.Client.SendAsync(request);
        Assert.Null(content.Headers.ContentLength);
        await Http.AssertErrorAsync(chunked, 413, "PAYLOAD_TOO_LARGE");
        Assert.Equal(0L, await api.Owner.QueryOneAsync<long>("select count(*) from leads where email = @email", new { email = form["email"] }));

        // Just under the cap is read and judged on its fields (here: too long a name).
        form["companyName"] = new string('x', 3500);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.SubmitAsync(form)).StatusCode);
    }

    // ------------------------------------------------------------------------------------- honeypot and time trap

    public static TheoryData<string, long, string> BotTiming => new()
    {
        { "startedAt", -1_000, "too_fast" }, { "startedAt", 0, "too_fast" }, { "startedAt", 60_000, "future" },
        { "startedAt", -25L * 3600 * 1000, "stale" }, { "website", 0, "honeypot" },
    };

    [Theory, MemberData(nameof(BotTiming))]
    public async Task AFilledHiddenFieldOrABotsTimingIsAnswered202AndNothingIsStored(string field, long offsetMillis, string outcome)
    {
        var form = Form();
        form[field] = field == "website" ? "https://spam.example" : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + offsetMillis;
        var before = api.Counted(outcome);
        var response = await api.SubmitAsync(form);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(Received, await response.Content.ReadAsStringAsync());
        Assert.Equal(0L, await api.Owner.QueryOneAsync<long>("select count(*) from leads where email = @email or phone = @phone",
            new { email = form["email"], phone = form["phone"] }));
        Assert.Equal(before + 1, api.Counted(outcome));
    }

    // -------------------------------------------------------------------------------------------------------- repeats

    [Fact]
    public async Task ARepeatByPhoneOrEmailWithin24HoursCountsOnTheSameLead()
    {
        var first = Form();
        var created = await api.SubmitAsync(first);
        var phone = (string)first["phone"]!;
        var email = (string)first["email"]!;
        var samePhone = Form();
        samePhone["phone"] = phone;
        samePhone["companyName"] = "Another Name " + Token();
        var sameEmail = Form();
        sameEmail["email"] = email.ToUpperInvariant();
        var second = await api.SubmitAsync(samePhone);
        var third = await api.SubmitAsync(sameEmail);

        // The same answer whether the request made a lead or not: no existence oracle.
        foreach (var r in new[] { created, second, third })
        {
            Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
            Assert.Equal(Received, await r.Content.ReadAsStringAsync());
        }
        const string lead = "select {0} from leads where phone = @phone or email = @email";
        Assert.Equal(1L, await api.Owner.QueryOneAsync<long>(string.Format(lead, "count(*)"), new { phone, email }));
        Assert.Equal(3, await api.Owner.QueryOneAsync<int>(string.Format(lead, "request_count"), new { phone, email }));
        Assert.Equal(first["companyName"], await api.Owner.QueryOneAsync<string>(string.Format(lead, "company_name"), new { phone, email }));
        Assert.True(await api.Owner.QueryOneAsync<bool>(string.Format(lead, "last_requested_at > created_at"), new { phone, email }));

        // A day later, the same phone is a new request.
        await api.Owner.ExecuteAsync("update leads set last_requested_at = last_requested_at - interval '25 hours' where phone = @phone", new { phone });
        var later = Form();
        later["phone"] = phone;
        Assert.Equal(HttpStatusCode.Accepted, (await api.SubmitAsync(later)).StatusCode);
        Assert.Equal(2L, await api.Owner.QueryOneAsync<long>("select count(*) from leads where phone = @phone", new { phone }));
    }

    [Fact]
    public async Task TwoIdenticalRequestsAtTheSameMomentMakeOneLead()
    {
        var form = Form();
        var responses = await Fleet.FleetTestSupport.RaceAsync(2, _ => api.SubmitAsync(form));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        Assert.Equal(1L, await api.Owner.QueryOneAsync<long>("select count(*) from leads where email = @email", new { email = form["email"] }));
        Assert.Equal(2, await api.Owner.QueryOneAsync<int>("select request_count from leads where email = @email", new { email = form["email"] }));
    }

    // --------------------------------------------------------------------------------------------------- rate limits

    [Fact]
    public async Task AnAddressGetsFiveAnHourAndTwentyADayEvenAfterARestart()
    {
        var limited = api.Counted("rate_limited_address");
        for (var i = 0; i < 5; i++)
        {
            var response = await api.SubmitAsync(Form());
            Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        }
        // A restart wipes in-memory counters; these live in the database.
        api.Factory.Services.GetRequiredService<InMemoryRateLimiter>().Reset();
        var sixth = Form();
        var refused = await api.SubmitAsync(sixth);
        var body = await Http.AssertErrorAsync(refused, 429, "RATE_LIMITED");
        var retryAfter = long.Parse(refused.Header("Retry-After")!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 1, 3600);
        Assert.Equal(retryAfter, body.GetProperty("retryAfterSeconds").GetInt64());
        Assert.Equal(0L, await api.Owner.QueryOneAsync<long>("select count(*) from leads where email = @email", new { email = sixth["email"] }));
        Assert.Equal(limited + 1, api.Counted("rate_limited_address"));

        // An hour later the hourly budget is back, and the day's still counts: 20 a day.
        await api.Owner.ExecuteAsync("""
            update rate_limit_counters set window_start = window_start - interval '1 hour', expires_at = expires_at - interval '1 hour'
            where bucket = 'demo-ip-hour' and key_hash = @key
            """, new { key = api.KeyHash("demo-ip-hour", api.Address) });
        Assert.Equal(HttpStatusCode.Accepted, (await api.SubmitAsync(Form())).StatusCode);
        await api.Owner.ExecuteAsync("update rate_limit_counters set hits = 20 where bucket = 'demo-ip-day' and key_hash = @key",
            new { key = api.KeyHash("demo-ip-day", api.Address) });
        var day = await api.SubmitAsync(Form());
        Assert.Equal((HttpStatusCode)429, day.StatusCode);
        Assert.InRange(long.Parse(day.Header("Retry-After")!, System.Globalization.CultureInfo.InvariantCulture), 1, 86_400);
    }

    [Fact]
    public async Task AnEncodedSpellingOfThePathNeverReachesTheFormWithoutTheGate()
    {
        // Routing matches decoded segments, so demo%2Drequests would be the same route (review of U1). The canonical-path
        // filter refuses it before anything else; it never stores a lead or passes the cap. Sent on a socket, as written.
        var large = Form();
        large["companyName"] = new string('x', 5 * 1024);
        foreach (var path in new[] { "/api/v1/public/demo%2Drequests", "/api/v1/public/demo%2drequest%73" })
        {
            var (status, text) = await kestrel.RawAsync("POST", path, JsonSerializer.Serialize(large));
            Assert.Equal(400, status);
            Assert.Contains("\"PATH_NOT_CANONICAL\"", text);
        }
        for (var i = 0; i < 6; i++)
        {
            var form = Form();
            var (status, _) = await kestrel.RawAsync("POST", "/api/v1/public/demo%2Drequests", JsonSerializer.Serialize(form));
            Assert.Equal(400, status);
            Assert.Equal(0L, await api.Owner.QueryOneAsync<long>("select count(*) from leads where email = @email", new { email = form["email"] }));
        }
    }

    [Fact]
    public async Task RefusedAndSetAsideRequestsSpendTheAddressBudgetToo()
    {
        for (var i = 0; i < 5; i++)
        {
            var form = Form();
            form[i % 2 == 0 ? "phone" : "website"] = i % 2 == 0 ? "bad" : "spam";
            Assert.Contains((int)(await api.SubmitAsync(form)).StatusCode, new[] { 400, 202 });
        }
        Assert.Equal((HttpStatusCode)429, (await api.SubmitAsync(Form())).StatusCode);
        Assert.Equal((HttpStatusCode)429, (await api.PostAsync(FormPath, "not json")).StatusCode);
    }

    [Fact]
    public async Task PastThreeADayAPhoneIsSetAsideSilentlyHoweverItIsWritten()
    {
        var digits = Digits();
        string[] spellings = ["010" + digits, "+20 10 " + digits, "0020-10-" + digits, Arabic("010" + digits)];
        var limited = api.Counted("rate_limited_contact");
        for (var i = 0; i < 4; i++)
        {
            var form = Form();
            form["phone"] = spellings[i];
            var response = await api.SubmitAsync(form);
            // The same answer past the limit: a 429 would say this phone was already used three times.
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal(Received, await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(limited + 1, api.Counted("rate_limited_contact"));
        var phone = "010" + digits;
        Assert.Equal(1L, await api.Owner.QueryOneAsync<long>("select count(*) from leads where phone = @phone", new { phone }));
        // Requests 2 and 3 were counted, each with other details; the fourth was set aside.
        Assert.Equal("3/2", await api.Owner.QueryOneAsync<string>(
            "select request_count || '/' || differing_requests from leads where phone = @phone", new { phone }));
    }

    [Fact]
    public async Task PastThreeADayAnEmailIsSetAsideSilentlyWhateverItsCapitals()
    {
        var email = "fleet-" + Token() + "@example.test";
        var limited = api.Counted("rate_limited_contact");
        for (var i = 0; i < 4; i++)
        {
            var form = Form();
            form["email"] = i % 2 == 0 ? email : email.ToUpperInvariant();
            Assert.Equal(HttpStatusCode.Accepted, (await api.SubmitAsync(form)).StatusCode);
        }
        Assert.Equal(limited + 1, api.Counted("rate_limited_contact"));
        Assert.Equal(3, await api.Owner.QueryOneAsync<int>("select request_count from leads where email = @email", new { email }));
    }

    [Fact]
    public async Task ARepeatWithTheSameDetailsIsNotCountedAsDiffering()
    {
        var form = Form();
        await api.SubmitAsync(form);
        await api.SubmitAsync(form);
        Assert.Equal("2/0", await api.Owner.QueryOneAsync<string>(
            "select request_count || '/' || differing_requests from leads where email = @email", new { email = form["email"] }));
    }

    [Fact]
    public async Task PastTheHourlyCapOfNewRequestsEverythingIsSetAsideSilently()
    {
        // The whole form's budget: an IPv6 /48 or a botnet spread over many addresses still meets it.
        await api.Owner.ExecuteAsync("""
            insert into rate_limit_counters (bucket, key_hash, window_start, expires_at, hits)
            values ('demo-all-hour', @key, to_timestamp(floor(extract(epoch from now()) / 3600) * 3600),
                    to_timestamp(floor(extract(epoch from now()) / 3600) * 3600 + 3600), @hits)
            on conflict (bucket, key_hash, window_start) do update set hits = excluded.hits
            """, new { key = api.KeyHash("demo-all-hour", "all"), hits = global::Zawed.Api.Modules.Leads.Leads.DefaultMaxPerHour });
        var capped = api.Counted("global_cap");
        var form = Form();
        var response = await api.SubmitAsync(form);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(Received, await response.Content.ReadAsStringAsync());
        Assert.Equal(0L, await api.Owner.QueryOneAsync<long>("select count(*) from leads where email = @email", new { email = form["email"] }));
        Assert.Equal(capped + 1, api.Counted("global_cap"));
    }

    // ---------------------------------------------------------------------------------------------- personal data

    [Fact]
    public async Task TheLogCarriesTheReferenceButNoneOfThePersonsData()
    {
        var form = Form();
        await api.SubmitAsync(form);
        var repeat = Form();
        repeat["phone"] = form["phone"];
        await api.SubmitAsync(repeat);
        await api.ForgetCountersAsync();
        var invalid = Form();
        invalid["fleetSize"] = "lots";
        await api.SubmitAsync(invalid);
        var spam = Form();
        spam["website"] = "spam";
        await api.SubmitAsync(spam);

        var reference = await api.Owner.QueryOneAsync<string>("select reference from leads where email = @email", new { email = form["email"] });
        Assert.Contains(reference!, api.Logs.All);
        foreach (var f in new[] { form, repeat, invalid, spam })
        {
            foreach (var field in new[] { "companyName", "contactName", "phone", "email" })
            {
                Assert.DoesNotContain(f[field]!.ToString()!, api.Logs.All);
            }
        }
    }

    /// <summary>A body without a length: <see cref="StreamContent"/> sends a non-seekable stream chunked.</summary>
    private sealed class Unseekable(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
