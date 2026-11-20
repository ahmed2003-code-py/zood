package com.zawed.api.tickets;

import java.util.UUID;

/**
 * Published, synchronously and inside the transaction, when a company or a station opens a ticket.
 * Exactly one of {@code companyId} and {@code operatorId} is set. The subject and the messages are
 * free text and are not part of the event.
 *
 * @param ticketType the ticket type's name, such as {@code LOST_TAG}
 */
public record TicketOpened(UUID ticketId, String ticketType, UUID companyId, UUID operatorId) {
}
