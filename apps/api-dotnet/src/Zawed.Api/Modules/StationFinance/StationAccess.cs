using System.Globalization;
using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Modules.Network.Contracts;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Text;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.StationFinance;

/// <summary>
/// The API half of tenant isolation for station finance (plan §3.6; Java <c>StationAccess</c>): the scope comes from the
/// signed-in user, never from the request. Row-level security is the other half and would hide the rows anyway; this
/// keeps the answer a plain 404 instead of an empty statement.
/// </summary>
public sealed class StationAccess(IStationDirectory stations)
{
    /// <summary>
    /// The station, if the caller may read its finances: any station for Operations, the operator's own stations for a
    /// station manager. Runs in the caller's transaction. 404 for a station that does not exist and for one outside the
    /// caller's scope alike.
    /// </summary>
    public async Task<StationRef> RequireAsync(ZawedPrincipal principal, Guid stationId, CancellationToken ct)
    {
        var station = await stations.FindStationAsync(stationId, ct) ?? throw ApiException.NotFound("Station");
        var scope = principal.Scope;
        var allowed = scope.IsPlatform || (scope.Type == ScopeType.OPERATOR && station.OperatorId == scope.Id);
        if (!allowed)
        {
            throw ApiException.NotFound("Station");
        }
        return station;
    }

    /// <summary>The operator a list is limited to: the caller's own for a station manager, the asked one for Operations.</summary>
    public static Guid? OperatorFilter(ZawedPrincipal principal, Guid? requestedOperatorId) =>
        principal.Scope.Type == ScopeType.OPERATOR ? principal.Scope.Id : requestedOperatorId;
}

/// <summary>Small helpers the Java classes shared (<c>BankAccounts.actorOf</c>, <c>Sql.ts</c>, <c>Instant.toString</c>).</summary>
internal static class Finance
{
    public static AuditActor ActorOf(ZawedPrincipal actor) => new(actor.UserId, actor.Role);

    /// <summary>
    /// "Now" at the database's precision (microseconds), as Java's clock and PostgreSQL keep it, so what is written,
    /// audited and read back is the same instant.
    /// </summary>
    public static DateTimeOffset Now(TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        return new DateTimeOffset(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero);
    }

    public static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    public static DateTimeOffset? Utc(DateTime? value) => value is { } v ? Utc(v) : null;

    /// <summary>A date as a Dapper parameter: midnight of kind Unspecified, stored by PostgreSQL's assignment cast as that day.</summary>
    public static DateTime Date(DateOnly value) => value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

    /// <summary>Java's <c>Instant.toString()</c>: seconds always, the fraction in groups of three digits when there is one.</summary>
    public static string JavaInstant(DateTimeOffset at)
    {
        var utc = at.UtcDateTime;
        var fraction = utc.Ticks % TimeSpan.TicksPerSecond * 100; // nanoseconds
        var text = utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        if (fraction != 0)
        {
            text += fraction % 1_000_000 == 0 ? "." + (fraction / 1_000_000).ToString("D3", CultureInfo.InvariantCulture)
                : fraction % 1_000 == 0 ? "." + (fraction / 1_000).ToString("D6", CultureInfo.InvariantCulture)
                : "." + fraction.ToString("D9", CultureInfo.InvariantCulture);
        }
        return text + "Z";
    }

    /// <summary>
    /// The statement's cursor is an entry sequence number, as for the wallet statement (Java
    /// <c>Long.parseLong(value.strip())</c>).
    /// </summary>
    public static long? SeqCursor(string? value)
    {
        if (value is null || JavaText.IsBlank(value))
        {
            return null;
        }
        return JavaText.TryParseLong(JavaText.Strip(value), out var seq)
            ? seq
            : throw new ApiException(400, "INVALID_CURSOR", "Invalid cursor", "cursor");
    }

    /// <summary>Java <c>BankAccounts.selfApproval()</c>, shared by payouts and reversals.</summary>
    public static ApiException SelfApproval() => new(403, "SELF_APPROVAL_NOT_ALLOWED",
        "A second person must approve this; you cannot approve your own request");
}
