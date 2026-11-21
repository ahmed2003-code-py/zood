using System.Text.RegularExpressions;
using Zawed.Api.Platform.Security;

namespace Zawed.Api.Modules.Notifications;

/// <summary>
/// What happened (Java <c>Notifications.Type</c>, contract §2). The web maps each type to its own sentence (both
/// languages). The parameter keys each type may carry are in <see cref="NotificationTypes.Keys"/>; the database holds the
/// same lists (<c>notification_param_keys</c>), and a key may be left out when it does not apply, never added.
/// </summary>
public enum NotificationType
{
    WALLET_LOW_BALANCE,
    TOPUP_CREDITED,
    TOPUP_REJECTED,
    TICKET_ANSWERED,
    TOPUP_AWAITING_APPROVAL,
    WALLET_ADJUSTMENT_AWAITING_APPROVAL,
    HARD_ALERT_RAISED,
    TICKET_OPENED,
    PAYOUT_POSTED,
    BANK_ACCOUNT_CHANGE_APPROVED,
    BANK_ACCOUNT_CHANGE_REJECTED,
}

public static class NotificationTypes
{
    private static readonly Dictionary<NotificationType, IReadOnlySet<string>> KeysByType = new()
    {
        [NotificationType.WALLET_LOW_BALANCE] = Set("balancePiastres", "thresholdPiastres", "transactionId"),
        [NotificationType.TOPUP_CREDITED] = Set("amountPiastres", "transactionId"),
        [NotificationType.TOPUP_REJECTED] = Set("amountPiastres"),
        [NotificationType.TICKET_ANSWERED] = Set("ticketType"),
        [NotificationType.TOPUP_AWAITING_APPROVAL] = Set("companyId", "amountPiastres"),
        [NotificationType.WALLET_ADJUSTMENT_AWAITING_APPROVAL] = Set("companyId", "amountPiastres"),
        [NotificationType.HARD_ALERT_RAISED] = Set("alertType", "severity", "companyId", "operatorId", "stationId", "fuelingId"),
        [NotificationType.TICKET_OPENED] = Set("ticketType", "companyId", "operatorId"),
        [NotificationType.PAYOUT_POSTED] = Set("stationId", "amountPiastres"),
        [NotificationType.BANK_ACCOUNT_CHANGE_APPROVED] = Set("stationId"),
        [NotificationType.BANK_ACCOUNT_CHANGE_REJECTED] = Set("stationId"),
    };

    /// <summary>The parameter keys a notification of this type may carry (contract §2).</summary>
    public static IReadOnlySet<string> Keys(this NotificationType type) => KeysByType[type];

    private static HashSet<string> Set(params string[] keys) => new(keys, StringComparer.Ordinal);
}

/// <summary>What the notification points at, for the web's link (Java <c>Notifications.EntityType</c>).</summary>
public enum NotificationEntityType
{
    COMPANY_WALLET,
    TOPUP,
    WALLET_ADJUSTMENT,
    ALERT,
    TICKET,
    PAYOUT,
    STATION_BANK_ACCOUNT,
}

/// <summary>Who should hear of it: everyone holding one of these roles in this scope (Java <c>Notifications.Audience</c>).</summary>
public sealed record Audience
{
    public Audience(Scope scope, IEnumerable<Role> roles)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var set = roles?.ToHashSet() ?? [];
        if (set.Count == 0)
        {
            throw new ArgumentException("An audience needs at least one role");
        }
        foreach (var role in set)
        {
            if (role.ScopeType() != scope.Type)
            {
                throw new ArgumentException($"{role} does not exist in a {scope.Type} scope");
            }
        }
        Scope = scope;
        Roles = set;
    }

    public Scope Scope { get; }

    public IReadOnlySet<Role> Roles { get; }

    public static Audience Of(Scope scope, params Role[] roles) => new(scope, roles);

    /// <summary>The roles as the <c>text[]</c> literal's elements, in a stable order.</summary>
    public string RolesCsv() => string.Join(",", Roles.Select(r => r.ToString()).Order(StringComparer.Ordinal));
}

/// <summary>
/// A notification's parameters (Java <c>Notifications.Params</c>): ids, integers (piastres, millilitres) and codes (enum
/// names) only, never text a person typed. The database checks the same rule (<c>notification_params_valid</c>).
/// </summary>
public sealed partial class NotificationParams
{
    // Insertion order, as Java's LinkedHashMap (jsonb stores its own order anyway).
    private readonly List<KeyValuePair<string, object>> values = [];

    public static NotificationParams Of() => new();

    /// <summary>An id; a null id is left out (it does not apply).</summary>
    public NotificationParams Id(string key, Guid? id) => id is null ? this : Put(key, id.Value.ToString());

    public NotificationParams Number(string key, long value) => Put(key, value);

    /// <summary>An enum's name, or a name of the same shape another module sent (an alert or ticket type).</summary>
    public NotificationParams Code(string key, string? code)
    {
        if (code is null || !CodePattern().IsMatch(code))
        {
            throw new ArgumentException("Not a code: " + key);
        }
        return Put(key, code);
    }

    public IReadOnlyList<KeyValuePair<string, object>> Values => values;

    /// <summary>Refuses a key the type does not carry (B7 W5 review, L2), before the database would.</summary>
    public void CheckFor(NotificationType type)
    {
        foreach (var (key, _) in values)
        {
            if (!type.Keys().Contains(key))
            {
                throw new ArgumentException($"{type} carries no parameter {key}");
            }
        }
    }

    private NotificationParams Put(string key, object value)
    {
        if (!KeyPattern().IsMatch(key))
        {
            throw new ArgumentException("Not a parameter name: " + key);
        }
        var at = values.FindIndex(v => v.Key == key);
        if (at >= 0)
        {
            values[at] = new(key, value); // a LinkedHashMap keeps the first position of a replaced key
        }
        else
        {
            values.Add(new(key, value));
        }
        return this;
    }

    [GeneratedRegex(@"^[a-z][A-Za-z0-9]{0,39}\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"^[A-Z][A-Z0-9_]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}

/// <summary>Everything a notification needs besides its type, built only when it is about to be written.</summary>
public sealed record Notice(Audience Audience, NotificationEntityType EntityType, Guid EntityId, NotificationParams Params);

/// <summary>One notification as its reader sees it; <c>ReadAt</c> is null while unread.</summary>
public sealed record Notification(Guid Id, NotificationType Type, NotificationEntityType EntityType, Guid EntityId,
    IReadOnlyDictionary<string, object> Params, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

/// <summary>The unread count, exact up to <see cref="Notifications.MaxUnreadCount"/>; above it <c>Capped</c> is set.</summary>
public sealed record UnreadCount(int Count, bool Capped);
