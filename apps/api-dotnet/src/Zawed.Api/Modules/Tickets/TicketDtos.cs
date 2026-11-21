using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Tickets;

/// <summary>What a ticket is about (Java <c>Tickets.Type</c>). <see cref="Tickets.CompanyTypes"/> and <see cref="Tickets.StationTypes"/> say who may ask what.</summary>
public enum TicketType
{
    LOST_TAG,
    STOLEN_TAG,
    LIMIT_CHANGE,
    VEHICLE_CHANGE,
    TOPUP_QUERY,
    DEVICE_FAULT,
    TAG_READ_ISSUE,
    PAYOUT_QUERY,
    PRICE_QUERY,
    DISPUTE,
    OTHER,
}

/// <summary>Java <c>Tickets.Status</c>.</summary>
public enum TicketStatus
{
    OPEN,
    IN_PROGRESS,
    RESOLVED,
    CLOSED,
}

// Request and response records, named as the Java records of TicketsController.

public sealed record OpenTicketRequest(
    [NotNull] TicketType? Type,
    [NotBlank, Size(max: 200)] string Subject,
    [NotBlank, Size(max: 4000)] string Body,
    Guid? VehicleId,
    Guid? StationId,
    Guid? FuelingId);

/// <param name="Internal">Operations only: a note the company or station never sees.</param>
public sealed record TicketReplyRequest([NotBlank, Size(max: 4000)] string Body, bool? Internal);

/// <param name="AssignToMe">Take the ticket.</param>
public sealed record TicketStatusRequest([NotNull] TicketStatus? Status, bool? AssignToMe);

public sealed record TicketResponse(
    Guid Id,
    string Reference,
    ScopeType ScopeType,
    Guid? CompanyId,
    Guid? OperatorId,
    TicketType Type,
    string Subject,
    TicketStatus Status,
    Guid? VehicleId,
    Guid? StationId,
    Guid? FuelingId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid? AssignedTo,
    DateTimeOffset? ResolvedAt);

public sealed record TicketMessageResponse(Guid Id, string AuthorRole, string Body, bool Internal, DateTimeOffset CreatedAt);

public sealed record TicketThreadResponse(TicketResponse Ticket, IReadOnlyList<TicketMessageResponse> Messages);
