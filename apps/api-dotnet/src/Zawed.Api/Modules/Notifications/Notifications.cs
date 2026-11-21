using System.Text.Json;
using Dapper;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Notifications;

/// <summary>
/// The notifications store (V19; Java <c>notifications.Notifications</c>): written inside the causing transaction, read
/// by each user for their own scope and role. Every read names the caller's audience explicitly and row-level security
/// names it again, so a query that forgot the filter would still show nothing foreign.
/// </summary>
public sealed class Notifications(IDbSession session, TimeProvider clock, NotificationMetrics metrics, ILogger<Notifications> log)
{
    /// <summary>The badge never needs more; counting further would read every unread row on each poll.</summary>
    public const int MaxUnreadCount = 99;

    /// <summary>
    /// How far behind the click "mark all read" sets the user's mark (V19, <c>notification_read_marks</c>). A notification
    /// is stamped at insert and visible at commit; the mark must stay behind every transaction still open when the user
    /// clicks, or it would cover a notification they never saw. Every transaction that writes one is a single fueling,
    /// sync item, top-up, adjustment, ticket or payout step, far shorter than this; it also absorbs clock differences
    /// between instances. What the user sees inside this window is marked row by row.
    /// </summary>
    public static readonly TimeSpan MarkGrace = TimeSpan.FromMinutes(5);

    /// <summary>The savepoint each notification is written in (PostgreSQL allows the name again inside a nested one).</summary>
    private const string Savepoint = "zawed_notification";

    /// <summary>Before any notification: the mark of a user who never marked all (they read one by one).</summary>
    private static readonly DateTimeOffset NoMark = DateTimeOffset.UnixEpoch;

    private DbTx Tx => session.Current;

    // ------------------------------------------------------------------------------------------------------ write

    /// <summary>
    /// Writes a notification in the caller's transaction, which must exist (Java <c>Propagation.MANDATORY</c>): a
    /// notification never commits without its cause, and an event published outside a transaction fails loudly.
    /// <para>The write runs in a savepoint (B7 W5 review, M1). If anything about it fails (building the notice, the
    /// parameter check, row-level security, a constraint, a trigger), it is rolled back to the savepoint, counted in
    /// <c>zawed.notifications.dropped</c> and logged as an error, and the cause goes on: a fueling, a sync item, a top-up
    /// decision or a ticket is a fact, and a notice about it is not a reason to refuse it.</para>
    /// <para>The actor is the transaction's own user (<c>app_user_id()</c>), never a value passed in.</para>
    /// </summary>
    public async Task SendAsync(NotificationType type, Func<Notice> notice, CancellationToken ct = default)
    {
        if (!session.HasTransaction)
        {
            throw new InvalidOperationException(
                "No existing transaction found for a notification: it is written only inside its cause's transaction");
        }
        var tx = session.Current;
        try
        {
            await tx.SavepointAsync(Savepoint, async () =>
            {
                await WriteAsync(tx, type, notice(), ct);
                return true;
            }, ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            metrics.CountDropped(type);
            log.LogError(ex, "Notification {Type} was refused and dropped; its cause is recorded without it", type);
        }
    }

    private Task WriteAsync(DbTx tx, NotificationType type, Notice notice, CancellationToken ct)
    {
        notice.Params.CheckFor(type);
        var now = clock.GetUtcNow();
        return tx.ExecuteAsync("""
            insert into notifications (id, type, audience_scope_type, audience_scope_id, audience_roles, entity_type,
                                       entity_id, params, actor_id, created_at)
            values (@id, @type, @scopeType, @scopeId, string_to_array(@roles, ','), @entityType, @entityId,
                    cast(@params as jsonb), app_user_id(), @now)
            """, new
        {
            id = Guid.CreateVersion7(now),
            type = type.ToString(),
            scopeType = notice.Audience.Scope.Type.ToString(),
            scopeId = notice.Audience.Scope.Id,
            roles = notice.Audience.RolesCsv(),
            entityType = notice.EntityType.ToString(),
            entityId = notice.EntityId,
            @params = ParamsJson(notice.Params),
            now,
        }, ct);
    }

    private static string ParamsJson(NotificationParams values)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in values.Values)
            {
                switch (value)
                {
                    case long number:
                        writer.WriteNumber(key, number);
                        break;
                    default:
                        writer.WriteString(key, (string)value);
                        break;
                }
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    // ------------------------------------------------------------------------------------------------------- read

    /// <summary>
    /// The user's mark, read on its own (a primary-key lookup) and bound as <c>@mark</c>. Inline, as a subquery under
    /// <c>coalesce</c>, PostgreSQL cannot prove the comparison leakproof, so under row-level security it would stay a
    /// filter over the whole feed instead of an index condition.
    /// </summary>
    private async Task<DateTimeOffset> MarkOfAsync(ZawedPrincipal me, CancellationToken ct)
    {
        var mark = await Tx.QuerySingleOrDefaultAsync<DateTime?>("select read_up_to from notification_read_marks where user_id = @me",
            new { me = me.UserId }, ct);
        return mark is { } at ? Utc(at) : NoMark;
    }

    /// <summary>
    /// The caller's notifications, newest first, paged by the keyset <c>(created_at, id)</c> (B7 W5 review, L4): the
    /// cursor is the last row's pair, so rows with the same time are neither skipped nor repeated, and the row comparison
    /// is an index condition on <c>notifications_audience_idx</c>. With <paramref name="unreadOnly"/>, only those after
    /// the user's mark and not read one by one.
    /// </summary>
    public async Task<Page<Notification>> MineAsync(ZawedPrincipal me, bool unreadOnly, string? cursor, int? limit, CancellationToken ct)
    {
        var size = Page<Notification>.Limit(limit);
        var after = Keyset.Parse(cursor);
        var args = Audience(me);
        args.Add("limit", size + 1);
        if (after is not null)
        {
            args.Add("cursorAt", after.CreatedAt);
            args.Add("cursorId", after.Id);
        }
        if (unreadOnly)
        {
            args.Add("mark", await MarkOfAsync(me, ct));
        }
        var rows = (await Tx.QueryAsync<Row>(FeedSql(me, unreadOnly, after is not null), args, ct)).Select(r => r.ToNotification()).ToList();
        if (rows.Count <= size)
        {
            return new Page<Notification>(rows, null);
        }
        var items = rows.Take(size).ToList();
        return new Page<Notification>(items, Keyset.Of(items[^1]));
    }

    /// <summary>
    /// Counts what is newer than the user's mark and not read one by one (B7 W5 review, M2): a range on the audience index
    /// from the mark onward, so the cost follows what arrived since the user last marked all read, not the size of the
    /// feed. Stops at <see cref="MaxUnreadCount"/> + 1.
    /// </summary>
    public async Task<UnreadCount> UnreadAsync(ZawedPrincipal me, CancellationToken ct)
    {
        var args = Audience(me);
        args.Add("mark", await MarkOfAsync(me, ct));
        args.Add("cap", MaxUnreadCount + 1);
        var counted = await Tx.QuerySingleAsync<long>(UnreadSql(me), args, ct);
        return new UnreadCount((int)Math.Min(counted, MaxUnreadCount), counted > MaxUnreadCount);
    }

    /// <summary>
    /// Marks one of the caller's notifications read; marking it again changes nothing. A notification outside the
    /// caller's audience does not exist for them: 404.
    /// </summary>
    public async Task<Result> MarkReadAsync(ZawedPrincipal me, Guid id, CancellationToken ct)
    {
        var args = Audience(me);
        args.Add("id", id);
        var mine = await Tx.QuerySingleAsync<bool>(
            $"select exists (select 1 from notifications n where n.id = @id and {AudienceFilter(me)})", args, ct);
        if (!mine)
        {
            return Result.NotFound("Notification");
        }
        await Tx.ExecuteAsync("""
            insert into notification_reads (user_id, notification_id, read_at) values (@me, @id, @now)
            on conflict do nothing
            """, new { me = me.UserId, id, now = clock.GetUtcNow() }, ct);
        return Result.NoContent();
    }

    /// <summary>
    /// Marks everything the caller can see now as read, in two bounded writes instead of one row per notification (B7 W5
    /// review, M2): the user's mark moves to <see cref="MarkGrace"/> before now, and what the caller sees inside that
    /// window gets read rows. A notification still uncommitted at this moment is not visible to either statement, and its
    /// time is inside the window (no writing transaction lasts that long), so it stays unread when it commits.
    /// <para>The mark is written first: its row lock queues two clicks at once (two tabs) one behind the other, so their
    /// read rows never cross into a deadlock, and the second one's statement sees the first one's rows. <c>greatest</c>
    /// keeps the later mark, and the database refuses any move back.</para>
    /// </summary>
    public async Task<Result> MarkAllReadAsync(ZawedPrincipal me, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var upTo = now - MarkGrace;
        await Tx.ExecuteAsync("""
            insert into notification_read_marks as m (user_id, read_up_to, marked_at) values (@me, @upTo, @now)
            on conflict (user_id) do update
              set read_up_to = greatest(m.read_up_to, excluded.read_up_to), marked_at = excluded.marked_at
            """, new { me = me.UserId, upTo, now }, ct);
        var args = Audience(me);
        args.Add("now", now);
        args.Add("upTo", upTo);
        await Tx.ExecuteAsync($"""
            insert into notification_reads (user_id, notification_id, read_at)
            select @me, n.id, @now from notifications n
            where {AudienceFilter(me)}
              and n.created_at > @upTo
              and not exists (select 1 from notification_reads r where r.user_id = @me and r.notification_id = n.id)
            order by n.id
            on conflict do nothing
            """, args, ct);
        return Result.NoContent();
    }

    // ---------------------------------------------------------------------------------------------------- helpers

    /// <summary>
    /// The feed query: binds <c>@me</c>, <c>@role</c>, the scope (<see cref="Audience"/>), <c>@limit</c>, with a cursor
    /// <c>@cursorAt</c> and <c>@cursorId</c>, and for unread only <c>@mark</c>. Fixed strings chosen by flags: nothing
    /// from the request goes into the SQL text. Public so a test can read its plan.
    /// </summary>
    public static string FeedSql(ZawedPrincipal me, bool unreadOnly, bool withCursor) =>
        $"""
        select n.id, n.type, n.entity_type, n.entity_id, n.params, n.created_at,
               coalesce(r.read_at, case when n.created_at <= m.read_up_to then m.marked_at end) as read_at
        from notifications n
        left join notification_reads r on r.notification_id = n.id and r.user_id = @me
        left join notification_read_marks m on m.user_id = @me
        where {AudienceFilter(me)}

        """
        + (unreadOnly ? "  and n.created_at > @mark\n  and r.read_at is null\n" : "")
        + (withCursor ? "  and (n.created_at, n.id) < (@cursorAt, @cursorId)\n" : "")
        + "order by n.created_at desc, n.id desc\nlimit @limit";

    /// <summary>The unread count: binds what <see cref="Audience"/> binds, <c>@mark</c> and <c>@cap</c>.</summary>
    public static string UnreadSql(ZawedPrincipal me) =>
        $"""
        select count(*) from (
          select 1 from notifications n
          where {AudienceFilter(me)}
            and n.created_at > @mark
            and not exists (select 1 from notification_reads r where r.user_id = @me and r.notification_id = n.id)
          limit @cap) unread
        """;

    /// <summary>
    /// The caller's audience as indexable predicates on <c>notifications_audience_idx</c>. One of two fixed strings,
    /// chosen by the kind of scope; nothing from the request goes into the SQL text.
    /// </summary>
    private static string AudienceFilter(ZawedPrincipal me) =>
        (me.Scope.IsPlatform
            ? "n.audience_scope_type = 'PLATFORM' and n.audience_scope_id is null"
            : "n.audience_scope_type = @scopeType and n.audience_scope_id = @scopeId")
        + " and @role = any(n.audience_roles)";

    /// <summary>Binds what <see cref="AudienceFilter"/> and the per-user joins refer to.</summary>
    public static DynamicParameters Audience(ZawedPrincipal me)
    {
        var args = new DynamicParameters();
        args.Add("me", me.UserId);
        args.Add("role", me.Role.ToString());
        if (!me.Scope.IsPlatform)
        {
            args.Add("scopeType", me.Scope.Type.ToString());
            args.Add("scopeId", me.Scope.Id);
        }
        return args;
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed class Row
    {
        public Guid Id { get; set; }
        public string Type { get; set; } = "";
        public string Entity_Type { get; set; } = "";
        public Guid Entity_Id { get; set; }
        public string Params { get; set; } = "{}";
        public DateTime Created_At { get; set; }
        public DateTime? Read_At { get; set; }

        public Notification ToNotification() => new(Id, Enum.Parse<NotificationType>(Type), Enum.Parse<NotificationEntityType>(Entity_Type),
            Entity_Id, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Params)!.ToDictionary(p => p.Key, p => (object)p.Value),
            Utc(Created_At), Read_At is { } read ? Utc(read) : null);
    }
}
