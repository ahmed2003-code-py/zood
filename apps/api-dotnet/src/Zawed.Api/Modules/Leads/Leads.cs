using System.Diagnostics.Metrics;
using Npgsql;
using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.RateLimit;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Leads;

/// <summary>
/// Demo requests (U1, u1-leads-contract.md; Java <c>Leads</c>). A request from the landing page becomes a lead in the Ops
/// console, unless it looks automated or repeats one from the last day.
/// <para>Personal data: a lead's name, phone and e-mail never reach a log line, an audit row or an error. Logs and audit rows
/// carry the lead's id and reference only.</para>
/// <para>The public path has no principal, so its transaction has no scope; it writes through <c>submit_lead</c> (V22), a
/// SECURITY DEFINER function with fixed columns, and the runtime role has no INSERT on <c>leads</c>. The Ops path runs in the
/// signed-in user's platform scope, and row-level security shows the rows to a platform user only.</para>
/// </summary>
public sealed class Leads(ITransactions transactions, IDbSession session, PostgresRateLimiter limiter, IAuditLog audit, TimeProvider clock,
    LeadMetrics metrics, IConfiguration configuration, ILogger<Leads> log)
{
    /// <summary>A person needs longer than this to fill in five fields; a script does not.</summary>
    public static readonly TimeSpan MinimumFillTime = TimeSpan.FromSeconds(3);

    /// <summary>A form shown longer ago than this is not a page someone has open.</summary>
    public static readonly TimeSpan MaximumFormAge = TimeSpan.FromHours(24);

    /// <summary>Requests per phone and per e-mail in a day, whatever address they come from.</summary>
    public const int PerContactPerDay = 3;

    /// <summary>
    /// Requests the form passes on in an hour, from everyone together, by default (<c>Zawed:Leads:MaxPerHour</c>, the Java
    /// <c>zawed.leads.max-per-hour</c>). Past it the rest are set aside (202) and a warning is logged: a botnet or an IPv6 /48
    /// spread over many addresses still meets one ceiling, and Ops is not buried.
    /// </summary>
    public const int DefaultMaxPerHour = 60;

    /// <summary>The form's values, cleaned and validated by the controller.</summary>
    public sealed record Submission(string CompanyName, string ContactName, string Phone, string Email, string FleetSize, string Locale,
        string? Website, long StartedAt);

    private readonly int maxPerHour = configuration.GetValue("Zawed:Leads:MaxPerHour", DefaultMaxPerHour);

    // ------------------------------------------------------------------------------------------------- public form

    /// <summary>
    /// Stores a demo request, counts it on a recent lead of the same phone or e-mail, or sets it aside. The caller answers the
    /// same in all three cases. Past the per-contact limit the request is set aside too: a 429 would tell whoever asks that
    /// the phone or e-mail was already used three times today.
    /// </summary>
    public async Task SubmitAsync(Submission submission, CancellationToken ct)
    {
        if (DropReason(submission, clock.GetUtcNow()) is { } dropped)
        {
            metrics.Count(dropped);
            log.LogInformation("Demo request set aside ({Reason})", dropped);
            return;
        }
        try
        {
            await limiter.AcquireAsync("demo-phone-day", submission.Phone, PerContactPerDay, TimeSpan.FromDays(1), ct);
            await limiter.AcquireAsync("demo-email-day", submission.Email, PerContactPerDay, TimeSpan.FromDays(1), ct);
        }
        catch (RateLimitedException)
        {
            metrics.Count("rate_limited_contact");
            log.LogInformation("Demo request set aside (rate_limited_contact)");
            return;
        }
        try
        {
            await limiter.AcquireAsync("demo-all-hour", "all", maxPerHour, TimeSpan.FromHours(1), ct);
        }
        catch (RateLimitedException)
        {
            metrics.Count("global_cap");
            log.LogWarning("Demo request set aside: the form passed its cap of {Cap} requests this hour", maxPerHour);
            return;
        }
        var stored = await StoreAsync(submission, ct);
        metrics.Count(stored.Repeated ? "repeated" : "created");
        log.LogInformation("Demo request {What} as lead {Reference} ({Id})", stored.Repeated ? "counted on" : "stored", stored.Reference, stored.Id);
    }

    /// <summary>
    /// Why a request is set aside without a row, or null: the hidden field is filled, or the form was sent less than
    /// <see cref="MinimumFillTime"/> after it was shown, before it was shown (a start time in the future), or more than
    /// <see cref="MaximumFormAge"/> after.
    /// </summary>
    public static string? DropReason(Submission submission, DateTimeOffset now)
    {
        if (submission.Website is { } website && !DemoRequestText.IsBlank(website))
        {
            return "honeypot";
        }
        var started = DateTimeOffset.FromUnixTimeMilliseconds(submission.StartedAt);
        if (started > now)
        {
            return "future";
        }
        if (started > now - MinimumFillTime)
        {
            return "too_fast";
        }
        if (started < now - MaximumFormAge)
        {
            return "stale";
        }
        return null;
    }

    private sealed record Stored(Guid Id, string Reference, bool Repeated);

    private async Task<Stored> StoreAsync(Submission s, CancellationToken ct)
    {
        try
        {
            // Its own transaction, with no scope: the definer function is the form's only door (Java TransactionTemplate).
            return await transactions.RunAsync(DbScopeContext.None, async tx =>
            {
                var row = await tx.QuerySingleAsync<(Guid, string, bool)>("""
                    select lead_id, lead_reference, repeated
                    from submit_lead(@id, @company, @contact, @phone, @email, @fleetSize, @locale)
                    """, new
                {
                    id = Guid.CreateVersion7(clock.GetUtcNow()), company = s.CompanyName, contact = s.ContactName, phone = s.Phone, email = s.Email,
                    fleetSize = s.FleetSize, locale = s.Locale,
                }, ct);
                return new Stored(row.Item1, row.Item2, row.Item3);
            }, ct: ct);
        }
        catch (NpgsqlException ex)
        {
            // A database message can quote the failing row, which is the prospect's personal data: only the SQL state is logged,
            // and the cause is not passed on to the generic error handler's log line.
            log.LogError("Demo request could not be stored (SQL state {State})", (ex as PostgresException)?.SqlState ?? ex.GetType().Name);
            throw new InvalidOperationException("The demo request could not be stored");
        }
    }

    // ---------------------------------------------------------------------------------------------------------- Ops

    /// <summary>Newest first, keyset on (created_at, id); the cursor is the last lead's id, as in every list.</summary>
    public async Task<Result<Page<LeadResponse>>> ListAsync(LeadStatus? status, string? cursor, int? limit, CancellationToken ct)
    {
        var size = Page<LeadResponse>.Limit(limit);
        var rows = (await session.Current.QueryAsync<LeadRow>(Select + """
            where (cast(@status as text) is null or status = @status)
              and (cast(@cursor as uuid) is null
                   or (created_at, id) < (select c.created_at, c.id from leads c where c.id = @cursor))
            order by created_at desc, id desc
            limit @limit
            """, new { status = status?.ToString(), cursor = Cursors.Parse(cursor), limit = size + 1 }, ct)).Select(r => r.Response()).ToList();
        return Page<LeadResponse>.Of(rows, size, l => l.Id);
    }

    /// <summary>
    /// Sets the status and, when <paramref name="note"/> is not null, the note (blank removes it). Closing starts the
    /// retention clock; reopening stops it. Audited in the same transaction, with the reference and the statuses only:
    /// never the note, which is free text about a person.
    /// </summary>
    public async Task<Result<LeadResponse>> UpdateAsync(ZawedPrincipal actor, Guid id, LeadStatus status, string? note, CancellationToken ct)
    {
        var tx = session.Current;
        if ((await tx.QueryAsync<LeadRow>(Select + "where id = @id for update", new { id }, ct)).SingleOrDefault() is not { } before)
        {
            return Result<LeadResponse>.NotFound("Lead");
        }
        var newNote = note is null ? before.Note : DemoRequestText.Note(note);
        var after = await tx.QuerySingleAsync<LeadRow>("""
            update leads
            set status = @status, note = @note, updated_at = @now, updated_by = @actor,
                closed_at = case when @status = 'CLOSED' then coalesce(closed_at, @now) else null end
            where id = @id
            returning id, reference, company_name as CompanyName, contact_name as ContactName, phone, email, fleet_size as FleetSize,
                      locale, status, note, created_at as CreatedAt, updated_at as UpdatedAt, request_count as RequestCount,
                      last_requested_at as LastRequestedAt, differing_requests as DifferingRequests
            """, new { status = status.ToString(), note = newNote, now = clock.GetUtcNow().UtcDateTime, actor = actor.UserId, id }, ct);
        await audit.RecordAsync(tx, new AuditActor(actor.UserId, actor.Role), "LEAD_UPDATED", "LEAD", id, Scope.Platform,
            new Dictionary<string, object?>
            {
                ["reference"] = before.Reference, ["from"] = before.Status, ["to"] = status.ToString(), ["noteChanged"] = before.Note != newNote,
            }, ct);
        return after.Response();
    }

    private const string Select = """
        select id, reference, company_name as CompanyName, contact_name as ContactName, phone, email, fleet_size as FleetSize, locale,
               status, note, created_at as CreatedAt, updated_at as UpdatedAt, request_count as RequestCount,
               last_requested_at as LastRequestedAt, differing_requests as DifferingRequests
        from leads

        """;

    private sealed record LeadRow(Guid Id, string Reference, string CompanyName, string ContactName, string Phone, string Email, string FleetSize,
        string Locale, string Status, string? Note, DateTime CreatedAt, DateTime UpdatedAt, int RequestCount, DateTime LastRequestedAt,
        int DifferingRequests)
    {
        public LeadResponse Response() => new(Id, Reference, CompanyName, ContactName, Phone, Email, FleetSize, Locale,
            Enum.Parse<LeadStatus>(Status), Note, Utc(CreatedAt), Utc(UpdatedAt), RequestCount, Utc(LastRequestedAt), DifferingRequests);

        private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }
}

/// <summary>
/// Every outcome of the public form, counted in <c>zawed.leads.demo.requests</c> tagged <c>outcome</c> (the Java Micrometer
/// counter): created, repeated, honeypot, too_fast, future, stale, rate_limited_contact, rate_limited_address, global_cap.
/// </summary>
public sealed class LeadMetrics
{
    public const string MeterName = "Zawed.Leads";
    public const string Metric = "zawed.leads.demo.requests";

    private readonly Counter<long> requests;

    public LeadMetrics(IMeterFactory meters) => requests = meters.Create(MeterName).CreateCounter<long>(Metric);

    public void Count(string outcome) => requests.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
}
