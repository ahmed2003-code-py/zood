using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Modules.Tickets.Contracts;
using Zawed.Api.Platform.Events;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Tickets;

/// <summary>
/// Support tickets (roadmap delta A5, plan §7 B6; Java <c>Tickets</c>). A company or a station asks Operations for something;
/// the thread is the record of what was asked and answered. Master data still changes only at Operations: a ticket is the
/// request, not the change. Runs in the caller's scope, so row-level security shows each side only its own tickets, and never
/// Operations' internal notes.
/// </summary>
public sealed class Tickets(IDbSession session, IAuditLog audit, IDomainEvents events, TimeProvider clock)
{
    /// <summary>What a company may ask about.</summary>
    public static readonly IReadOnlySet<TicketType> CompanyTypes = new HashSet<TicketType>
    {
        TicketType.LOST_TAG, TicketType.STOLEN_TAG, TicketType.LIMIT_CHANGE, TicketType.VEHICLE_CHANGE, TicketType.TOPUP_QUERY,
        TicketType.DISPUTE, TicketType.OTHER,
    };

    /// <summary>What a station may ask about.</summary>
    public static readonly IReadOnlySet<TicketType> StationTypes = new HashSet<TicketType>
    {
        TicketType.DEVICE_FAULT, TicketType.TAG_READ_ISSUE, TicketType.PAYOUT_QUERY, TicketType.PRICE_QUERY, TicketType.DISPUTE,
        TicketType.OTHER,
    };

    public sealed record Draft(TicketType Type, string Subject, string Body, Guid? VehicleId, Guid? StationId, Guid? FuelingId);

    private DbTx Tx => session.Current;

    /// <summary>A company or station opens a ticket with its first message.</summary>
    public async Task<Result<TicketThreadResponse>> OpenAsync(ZawedPrincipal actor, Draft draft, CancellationToken ct)
    {
        var scope = actor.Scope;
        var company = scope.Type == ScopeType.COMPANY;
        if (scope.Type != ScopeType.COMPANY && scope.Type != ScopeType.OPERATOR)
        {
            return Result<TicketThreadResponse>.Forbidden(message: "Operations answers tickets; it does not open them");
        }
        var allowed = company ? CompanyTypes : StationTypes;
        if (!allowed.Contains(draft.Type))
        {
            return Result<TicketThreadResponse>.Fail(400, "TICKET_TYPE_NOT_ALLOWED", "This kind of request is not available here", "type");
        }
        // A foreign key is checked without row-level security, so a ticket could otherwise point at another tenant's vehicle,
        // station or fueling. Each link must be a row the caller can see (B6 contract). That holds for a company's station link
        // too (B7 W2 IDOR sweep): it used to be skipped, so a company could link any operator's station, and an id that did not
        // exist failed the foreign key with a 500, which told real stations from made-up ones. A company sees no stations, so it
        // links the fueling instead, which names its station.
        if (draft.VehicleId is { } vehicle && !await VisibleAsync("vehicles", vehicle, ct))
        {
            return LinkNotFound("vehicleId");
        }
        if (draft.FuelingId is { } fueling && !await FuelingLinks.VisibleAsync(Tx, fueling, ct))
        {
            return LinkNotFound("fuelingId");
        }
        if (draft.StationId is { } station && !await VisibleAsync("stations", station, ct))
        {
            return LinkNotFound("stationId");
        }
        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        var reference = "T-" + await Tx.QuerySingleAsync<long>("select nextval('ticket_reference_seq')", ct: ct);
        await Tx.ExecuteAsync("""
            insert into tickets (id, reference, scope_type, company_id, operator_id, type, subject, status, vehicle_id,
                                 station_id, fueling_id, created_by, created_at, updated_at)
            values (@id, @reference, @scopeType, @company, @operator, @type, @subject, 'OPEN', @vehicle, @station, @fueling,
                    @actor, @now, @now)
            """, new
        {
            id, reference, scopeType = scope.Type.ToString(), company = company ? scope.Id : null, @operator = company ? null : scope.Id,
            type = draft.Type.ToString(), subject = JavaStrip(draft.Subject), vehicle = draft.VehicleId, station = draft.StationId,
            fueling = draft.FuelingId, actor = actor.UserId, now = now.UtcDateTime,
        }, ct);
        await AddMessageAsync(actor, id, draft.Body, false, now, ct);
        await audit.RecordAsync(Tx, new AuditActor(actor.UserId, actor.Role), "TICKET_OPENED", "TICKET", id, scope,
            new Dictionary<string, object?> { ["reference"] = reference, ["type"] = draft.Type.ToString() }, ct);
        await events.PublishAsync(new TicketOpened(id, draft.Type.ToString(), company ? scope.Id : null, company ? null : scope.Id), ct);
        return Result<TicketThreadResponse>.Created((await ThreadAsync(id, false, ct)).Data!);
    }

    /// <summary>A reply. Only Operations may write an internal note.</summary>
    public async Task<Result<TicketThreadResponse>> ReplyAsync(ZawedPrincipal actor, Guid ticketId, string body, bool @internal,
        CancellationToken ct)
    {
        if (await GetAsync(ticketId, ct) is not { } ticket)
        {
            return Result<TicketThreadResponse>.NotFound("Ticket");
        }
        var platform = actor.Scope.IsPlatform;
        if (@internal && !platform)
        {
            return Result<TicketThreadResponse>.Forbidden(message: "Only Operations writes internal notes");
        }
        if (ticket.Status == nameof(TicketStatus.CLOSED))
        {
            return Result<TicketThreadResponse>.Conflict("TICKET_CLOSED", "This ticket is closed; open a new one");
        }
        var now = clock.GetUtcNow();
        await AddMessageAsync(actor, ticketId, body, @internal, now, ct);
        // Operations answering an open ticket takes it up. Only Operations changes a status: a company or station writing on a
        // resolved ticket shows as a new message, and Operations reopens it.
        if (platform && !@internal && ticket.Status == nameof(TicketStatus.OPEN))
        {
            await SetStatusAsync(ticketId, TicketStatus.IN_PROGRESS, ticket.AssignedTo ?? actor.UserId, now, ct);
        }
        if (platform && !@internal)
        {
            await events.PublishAsync(new TicketAnswered(ticketId, ticket.Type, ticket.CompanyId, ticket.OperatorId), ct);
        }
        return await ThreadAsync(ticketId, platform, ct);
    }

    /// <summary>Operations moves a ticket along and, optionally, takes it.</summary>
    public async Task<Result<TicketThreadResponse>> UpdateAsync(ZawedPrincipal actor, Guid ticketId, TicketStatus status, bool assignToMe,
        CancellationToken ct)
    {
        if (await GetAsync(ticketId, ct) is not { } ticket)
        {
            return Result<TicketThreadResponse>.NotFound("Ticket");
        }
        var now = clock.GetUtcNow();
        await SetStatusAsync(ticketId, status, assignToMe ? actor.UserId : ticket.AssignedTo, now, ct);
        await audit.RecordAsync(Tx, new AuditActor(actor.UserId, actor.Role), "TICKET_STATUS_CHANGED", "TICKET", ticketId,
            ticket.CompanyId is { } companyId ? Scope.Company(companyId) : Scope.Operator(ticket.OperatorId!.Value),
            new Dictionary<string, object?> { ["from"] = ticket.Status, ["to"] = status.ToString() }, ct);
        return await ThreadAsync(ticketId, true, ct);
    }

    public async Task<Result<Page<TicketResponse>>> ListAsync(Guid? companyId, Guid? operatorId, TicketStatus? status, TicketType? type,
        string? cursor, int? limit, CancellationToken ct)
    {
        var size = Page<TicketResponse>.Limit(limit);
        var rows = await Tx.QueryAsync<TicketRow>(Select + """
            where (cast(@company as uuid) is null or company_id = @company)
              and (cast(@operator as uuid) is null or operator_id = @operator)
              and (cast(@status as text) is null or status = @status)
              and (cast(@type as text) is null or type = @type)
              and (cast(@cursor as uuid) is null or id < @cursor)
            order by id desc limit @limit
            """, new
        {
            company = companyId, @operator = operatorId, status = status?.ToString(), type = type?.ToString(), cursor = Cursors.Parse(cursor),
            limit = size + 1,
        }, ct);
        return Page<TicketResponse>.Of([.. rows.Select(r => r.Response())], size, t => t.Id);
    }

    public async Task<Result<TicketThreadResponse>> ThreadAsync(Guid ticketId, bool withInternal, CancellationToken ct)
    {
        if (await GetAsync(ticketId, ct) is not { } ticket)
        {
            return Result<TicketThreadResponse>.NotFound("Ticket");
        }
        var messages = await Tx.QueryAsync<MessageRow>("""
            select id, author_id as AuthorId, author_role as AuthorRole, body, internal, created_at as CreatedAt
            from ticket_messages where ticket_id = @ticket and (@internal or not internal) order by created_at, id
            """, new { ticket = ticketId, @internal = withInternal }, ct);
        return new TicketThreadResponse(ticket.Response(), [.. messages.Select(m => m.Response())]);
    }

    private async Task<TicketRow?> GetAsync(Guid id, CancellationToken ct) =>
        (await Tx.QueryAsync<TicketRow>(Select + "where id = @id", new { id }, ct)).SingleOrDefault();

    /// <summary>The table name is one of two constants above, never input (Java <c>requireVisible</c>).</summary>
    private Task<bool> VisibleAsync(string table, Guid id, CancellationToken ct) =>
        Tx.QuerySingleAsync<bool>("select exists (select 1 from " + table + " where id = @id)", new { id }, ct);

    private static Result<TicketThreadResponse> LinkNotFound(string field) =>
        Result<TicketThreadResponse>.Fail(400, "TICKET_LINK_NOT_FOUND", "The linked record was not found", field);

    private Task AddMessageAsync(ZawedPrincipal actor, Guid ticketId, string body, bool @internal, DateTimeOffset now, CancellationToken ct) =>
        Tx.ExecuteAsync("""
            insert into ticket_messages (id, ticket_id, author_id, author_role, body, internal, created_at)
            values (@id, @ticket, @author, @role, @body, @internal, @now)
            """, new
        {
            id = Guid.CreateVersion7(now), ticket = ticketId, author = actor.UserId, role = actor.Role.ToString(), body = JavaStrip(body),
            @internal, now = now.UtcDateTime,
        }, ct);

    private Task SetStatusAsync(Guid ticketId, TicketStatus status, Guid? assignee, DateTimeOffset now, CancellationToken ct) =>
        Tx.ExecuteAsync("""
            update tickets set status = @status, assigned_to = @assignee, updated_at = @now,
                   resolved_at = case when @done then coalesce(resolved_at, @now) else null end
            where id = @id
            """, new
        {
            status = status.ToString(), assignee, now = now.UtcDateTime, done = status is TicketStatus.RESOLVED or TicketStatus.CLOSED,
            id = ticketId,
        }, ct);

    /// <summary>Java's <c>String.strip()</c>: Unicode white space (<c>Character.isWhitespace</c>) off both ends.</summary>
    private static string JavaStrip(string value)
    {
        int start = 0, end = value.Length;
        while (start < end && Sql.IsJavaWhitespace(value[start]))
        {
            start++;
        }
        while (end > start && Sql.IsJavaWhitespace(value[end - 1]))
        {
            end--;
        }
        return value[start..end];
    }

    private const string Select = """
        select id, reference, scope_type as ScopeType, company_id as CompanyId, operator_id as OperatorId, type, subject, status,
               vehicle_id as VehicleId, station_id as StationId, fueling_id as FuelingId, created_by as CreatedBy,
               created_at as CreatedAt, updated_at as UpdatedAt, assigned_to as AssignedTo, resolved_at as ResolvedAt
        from tickets

        """;

    /// <summary>A <c>tickets</c> row as Java's <c>Ticket.map</c> read it.</summary>
    private sealed record TicketRow(Guid Id, string Reference, string ScopeType, Guid? CompanyId, Guid? OperatorId, string Type,
        string Subject, string Status, Guid? VehicleId, Guid? StationId, Guid? FuelingId, Guid CreatedBy, DateTime CreatedAt,
        DateTime UpdatedAt, Guid? AssignedTo, DateTime? ResolvedAt)
    {
        public TicketResponse Response() => new(Id, Reference, Enum.Parse<ScopeType>(ScopeType), CompanyId, OperatorId,
            Enum.Parse<TicketType>(Type), Subject, Enum.Parse<TicketStatus>(Status), VehicleId, StationId, FuelingId, Utc(CreatedAt),
            Utc(UpdatedAt), AssignedTo, ResolvedAt is { } resolved ? Utc(resolved) : null);
    }

    private sealed record MessageRow(Guid Id, Guid AuthorId, string AuthorRole, string Body, bool Internal, DateTime CreatedAt)
    {
        public TicketMessageResponse Response() => new(Id, AuthorRole, Body, Internal, Utc(CreatedAt));
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
