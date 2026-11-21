namespace Zawed.Api.Modules.Tickets.Contracts;

/// <summary>
/// Published, synchronously and inside the transaction, when a company or a station opens a ticket (Java
/// <c>TicketOpened</c>). Exactly one of <paramref name="CompanyId"/> and <paramref name="OperatorId"/> is set. The subject
/// and the messages are free text and are not part of the event.
/// </summary>
/// <param name="TicketType">The ticket type's name, such as <c>LOST_TAG</c>.</param>
public sealed record TicketOpened(Guid TicketId, string TicketType, Guid? CompanyId, Guid? OperatorId);

/// <summary>
/// Published, synchronously and inside the transaction, when Operations writes a public answer on a ticket (Java
/// <c>TicketAnswered</c>). An internal note is not an answer and publishes nothing. Exactly one of
/// <paramref name="CompanyId"/> and <paramref name="OperatorId"/> is set.
/// </summary>
/// <param name="TicketType">The ticket type's name, such as <c>LOST_TAG</c>.</param>
public sealed record TicketAnswered(Guid TicketId, string TicketType, Guid? CompanyId, Guid? OperatorId);
