package com.zawed.api.tickets;

import java.util.UUID;

/**
 * Published, synchronously and inside the transaction, when Operations writes a public answer on a
 * ticket. An internal note is not an answer and publishes nothing. Exactly one of {@code companyId}
 * and {@code operatorId} is set.
 *
 * @param ticketType the ticket type's name, such as {@code LOST_TAG}
 */
public record TicketAnswered(UUID ticketId, String ticketType, UUID companyId, UUID operatorId) {
}
