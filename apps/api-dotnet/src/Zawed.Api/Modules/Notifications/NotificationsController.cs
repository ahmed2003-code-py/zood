using System.ComponentModel;
using Microsoft.AspNetCore.Mvc;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Notifications;

/// <summary>One notification as the signed-in user sees it (Java <c>NotificationsController.NotificationResponse</c>).</summary>
public sealed record NotificationResponse(
    Guid Id,
    NotificationType Type,
    [property: Description("What the notification points at")] NotificationEntityType EntityType,
    Guid EntityId,
    [property: Description("Ids (UUID strings), integers (piastres, millilitres) and codes (enum names) for the message; never display "
        + "text. The keys per type are in b7-notifications-contract.md")]
    IReadOnlyDictionary<string, object> Params,
    DateTimeOffset CreatedAt,
    bool Read,
    [property: Description("When this user read it; null while unread")] DateTimeOffset? ReadAt)
{
    public static NotificationResponse Of(Notification n) =>
        new(n.Id, n.Type, n.EntityType, n.EntityId, n.Params, n.CreatedAt, n.ReadAt is not null, n.ReadAt);
}

/// <summary>The badge's number (Java <c>UnreadNotificationsResponse</c>).</summary>
public sealed record UnreadNotificationsResponse(
    [property: Description("Unread notifications, at most 99")] int Count,
    [property: Description("True when there are more than 99 (show 99+)")] bool Capped)
{
    public static UnreadNotificationsResponse Of(UnreadCount c) => new(c.Count, c.Capped);
}

/// <summary>
/// The signed-in user's notifications, the same four endpoints for every portal (Java <c>NotificationsController</c>).
/// Like <c>/auth/me</c>, they need a signed-in user and no permission: what each user sees is decided by their scope and
/// role (the service filters, row-level security filters again), and a notification outside them answers 404.
/// </summary>
[Route(ApiContract.BasePath + "/notifications")]
[Tags("notifications")]
public sealed class NotificationsController(Notifications notifications) : ApiControllerBase
{
    private ZawedPrincipal Principal => ZawedPrincipal.From(User)!;

    [HttpGet(Name = "listMyNotifications")]
    [EndpointSummary("The signed-in user's notifications, newest first")]
    [ProducesResponseType<Page<NotificationResponse>>(200)]
    public async Task<Page<NotificationResponse>> List([FromQuery, Description("Only the ones not yet read")] bool? unread,
        [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct)
    {
        var page = await notifications.MineAsync(Principal, unread == true, cursor, limit, ct);
        return new Page<NotificationResponse>(page.Items.Select(NotificationResponse.Of).ToList(), page.NextCursor);
    }

    [HttpGet("unread-count", Name = "countMyUnreadNotifications")]
    [EndpointSummary("How many of the user's notifications are unread")]
    [ProducesResponseType<UnreadNotificationsResponse>(200)]
    public async Task<UnreadNotificationsResponse> UnreadCount(CancellationToken ct) =>
        UnreadNotificationsResponse.Of(await notifications.UnreadAsync(Principal, ct));

    [HttpPost("{id}/read", Name = "markMyNotificationRead")]
    [EndpointSummary("Mark one notification read; marking it again changes nothing")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct) => From(await notifications.MarkReadAsync(Principal, id, ct));

    [HttpPost("read-all", Name = "markAllMyNotificationsRead")]
    [EndpointSummary("Mark every notification the user can see read")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct) => From(await notifications.MarkAllReadAsync(Principal, ct));
}
