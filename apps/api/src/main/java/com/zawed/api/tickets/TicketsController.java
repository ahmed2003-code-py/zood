package com.zawed.api.tickets;

import java.time.Instant;
import java.util.List;
import java.util.UUID;

import jakarta.validation.Valid;
import jakarta.validation.constraints.NotBlank;
import jakarta.validation.constraints.NotNull;
import jakarta.validation.constraints.Size;

import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.media.Schema;
import io.swagger.v3.oas.annotations.media.Schema.RequiredMode;
import io.swagger.v3.oas.annotations.tags.Tag;

import org.springframework.http.HttpStatus;
import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.ResponseStatus;
import org.springframework.web.bind.annotation.RestController;

import com.zawed.api.platform.security.Permission;
import com.zawed.api.platform.security.RequiresPermission;
import com.zawed.api.platform.security.ScopeType;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiContract;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.platform.web.Page;
import com.zawed.api.tickets.Tickets.Draft;
import com.zawed.api.tickets.Tickets.Message;
import com.zawed.api.tickets.Tickets.Status;
import com.zawed.api.tickets.Tickets.TicketThread;
import com.zawed.api.tickets.Tickets.Ticket;
import com.zawed.api.tickets.Tickets.Type;

/** Support tickets for the company, the station and Operations (roadmap delta A5, plan §7 B6). */
@RestController
@RequestMapping(ApiContract.BASE_PATH)
class TicketsController {

	record OpenTicketRequest(@NotNull Type type, @NotBlank @Size(max = 200) String subject,
			@NotBlank @Size(max = 4000) String body, UUID vehicleId, UUID stationId, UUID fuelingId) {
	}

	record TicketReplyRequest(@NotBlank @Size(max = 4000) String body,
			@Schema(description = "Operations only: a note the company or station never sees") Boolean internal) {
	}

	record TicketStatusRequest(@NotNull Status status, @Schema(description = "Take the ticket") Boolean assignToMe) {
	}

	record TicketResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) String reference,
			@Schema(requiredMode = RequiredMode.REQUIRED) ScopeType scopeType, UUID companyId, UUID operatorId,
			@Schema(requiredMode = RequiredMode.REQUIRED) Type type,
			@Schema(requiredMode = RequiredMode.REQUIRED) String subject,
			@Schema(requiredMode = RequiredMode.REQUIRED) Status status, UUID vehicleId, UUID stationId, UUID fuelingId,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant createdAt,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant updatedAt, UUID assignedTo, Instant resolvedAt) {

		static TicketResponse of(Ticket t) {
			return new TicketResponse(t.id(), t.reference(), t.scopeType(), t.companyId(), t.operatorId(), t.type(),
					t.subject(), t.status(), t.vehicleId(), t.stationId(), t.fuelingId(), t.createdAt(), t.updatedAt(),
					t.assignedTo(), t.resolvedAt());
		}

	}

	record TicketMessageResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) String authorRole,
			@Schema(requiredMode = RequiredMode.REQUIRED) String body,
			@Schema(requiredMode = RequiredMode.REQUIRED) boolean internal,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant createdAt) {

		static TicketMessageResponse of(Message m) {
			return new TicketMessageResponse(m.id(), m.authorRole(), m.body(), m.internal(), m.createdAt());
		}

	}

	record TicketThreadResponse(@Schema(requiredMode = RequiredMode.REQUIRED) TicketResponse ticket,
			@Schema(requiredMode = RequiredMode.REQUIRED) List<TicketMessageResponse> messages) {

		static TicketThreadResponse of(TicketThread thread) {
			return new TicketThreadResponse(TicketResponse.of(thread.ticket()),
					thread.messages().stream().map(TicketMessageResponse::of).toList());
		}

	}

	private final Tickets tickets;

	TicketsController(Tickets tickets) {
		this.tickets = tickets;
	}

	// ------------------------------------------------------------------------------------- company

	@GetMapping("/company/tickets")
	@Tag(name = "company portal")
	@RequiresPermission(Permission.COMPANY_READ)
	@Operation(operationId = "listMyTickets", summary = "The company's tickets, newest first")
	Page<TicketResponse> companyList(@AuthenticationPrincipal ZawedPrincipal principal,
			@RequestParam(required = false) Status status, @RequestParam(required = false) String cursor,
			@RequestParam(required = false) Integer limit) {
		return page(tickets.list(principal.scope().id(), null, status, null, cursor, limit));
	}

	@PostMapping("/company/tickets")
	@ResponseStatus(HttpStatus.CREATED)
	@Tag(name = "company portal")
	@RequiresPermission(Permission.TICKETS_OPEN)
	@Operation(operationId = "openMyTicket", summary = "Ask Operations for something: a lost tag, a limit change, a question")
	TicketThreadResponse companyOpen(@AuthenticationPrincipal ZawedPrincipal principal,
			@Valid @RequestBody OpenTicketRequest body) {
		requireScope(principal, ScopeType.COMPANY);
		return open(principal, body);
	}

	@GetMapping("/company/tickets/{id}")
	@Tag(name = "company portal")
	@RequiresPermission(Permission.COMPANY_READ)
	@Operation(operationId = "getMyTicket", summary = "One of the company's tickets with its thread")
	TicketThreadResponse companyGet(@PathVariable UUID id) {
		return TicketThreadResponse.of(tickets.thread(id, false));
	}

	@PostMapping("/company/tickets/{id}/messages")
	@Tag(name = "company portal")
	@RequiresPermission(Permission.TICKETS_OPEN)
	@Operation(operationId = "replyMyTicket", summary = "Add a message to one of the company's tickets")
	TicketThreadResponse companyReply(@AuthenticationPrincipal ZawedPrincipal principal, @PathVariable UUID id,
			@Valid @RequestBody TicketReplyRequest body) {
		requireScope(principal, ScopeType.COMPANY);
		return TicketThreadResponse.of(tickets.reply(principal, id, body.body(), false));
	}

	// ------------------------------------------------------------------------------------- station

	@GetMapping("/station/tickets")
	@Tag(name = "station portal")
	@RequiresPermission(Permission.STATION_READ)
	@Operation(operationId = "listStationTickets", summary = "The operator's tickets, newest first")
	Page<TicketResponse> stationList(@AuthenticationPrincipal ZawedPrincipal principal,
			@RequestParam(required = false) Status status, @RequestParam(required = false) String cursor,
			@RequestParam(required = false) Integer limit) {
		return page(tickets.list(null, principal.scope().id(), status, null, cursor, limit));
	}

	@PostMapping("/station/tickets")
	@ResponseStatus(HttpStatus.CREATED)
	@Tag(name = "station portal")
	@RequiresPermission(Permission.TICKETS_OPEN)
	@Operation(operationId = "openStationTicket", summary = "Ask Operations for something: a device fault, a payout question")
	TicketThreadResponse stationOpen(@AuthenticationPrincipal ZawedPrincipal principal,
			@Valid @RequestBody OpenTicketRequest body) {
		requireScope(principal, ScopeType.OPERATOR);
		return open(principal, body);
	}

	@GetMapping("/station/tickets/{id}")
	@Tag(name = "station portal")
	@RequiresPermission(Permission.STATION_READ)
	@Operation(operationId = "getStationTicket", summary = "One of the operator's tickets with its thread")
	TicketThreadResponse stationGet(@PathVariable UUID id) {
		return TicketThreadResponse.of(tickets.thread(id, false));
	}

	@PostMapping("/station/tickets/{id}/messages")
	@Tag(name = "station portal")
	@RequiresPermission(Permission.TICKETS_OPEN)
	@Operation(operationId = "replyStationTicket", summary = "Add a message to one of the operator's tickets")
	TicketThreadResponse stationReply(@AuthenticationPrincipal ZawedPrincipal principal, @PathVariable UUID id,
			@Valid @RequestBody TicketReplyRequest body) {
		requireScope(principal, ScopeType.OPERATOR);
		return TicketThreadResponse.of(tickets.reply(principal, id, body.body(), false));
	}

	// ----------------------------------------------------------------------------------------- Ops

	@GetMapping("/ops/tickets")
	@Tag(name = "tickets")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "listTickets", summary = "Every ticket, newest first")
	Page<TicketResponse> opsList(@RequestParam(required = false) UUID companyId,
			@RequestParam(required = false) UUID operatorId, @RequestParam(required = false) Status status,
			@RequestParam(required = false) Type type, @RequestParam(required = false) String cursor,
			@RequestParam(required = false) Integer limit) {
		return page(tickets.list(companyId, operatorId, status, type, cursor, limit));
	}

	@GetMapping("/ops/tickets/{id}")
	@Tag(name = "tickets")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "getTicket", summary = "One ticket with its whole thread, internal notes included")
	TicketThreadResponse opsGet(@PathVariable UUID id) {
		return TicketThreadResponse.of(tickets.thread(id, true));
	}

	@PostMapping("/ops/tickets/{id}/messages")
	@Tag(name = "tickets")
	@RequiresPermission(Permission.TICKETS_MANAGE)
	@Operation(operationId = "replyTicket", summary = "Answer a ticket, or add an internal note")
	TicketThreadResponse opsReply(@AuthenticationPrincipal ZawedPrincipal principal, @PathVariable UUID id,
			@Valid @RequestBody TicketReplyRequest body) {
		return TicketThreadResponse.of(tickets.reply(principal, id, body.body(), Boolean.TRUE.equals(body.internal())));
	}

	@PostMapping("/ops/tickets/{id}/status")
	@Tag(name = "tickets")
	@RequiresPermission(Permission.TICKETS_MANAGE)
	@Operation(operationId = "updateTicketStatus", summary = "Move a ticket along, and optionally take it")
	TicketThreadResponse opsStatus(@AuthenticationPrincipal ZawedPrincipal principal, @PathVariable UUID id,
			@Valid @RequestBody TicketStatusRequest body) {
		return TicketThreadResponse.of(tickets.update(principal, id, body.status(), Boolean.TRUE.equals(body.assignToMe())));
	}

	private TicketThreadResponse open(ZawedPrincipal principal, OpenTicketRequest body) {
		return TicketThreadResponse.of(tickets.open(principal,
				new Draft(body.type(), body.subject(), body.body(), body.vehicleId(), body.stationId(), body.fuelingId())));
	}

	/**
	 * TICKETS_OPEN is held by company and station users alike, so each portal's writes also check the
	 * caller's kind of scope: a station manager never writes through the company portal, or back.
	 */
	private static void requireScope(ZawedPrincipal principal, ScopeType expected) {
		if (principal.scope().type() != expected) {
			throw new ApiException(HttpStatus.FORBIDDEN, "FORBIDDEN", "Not allowed for this portal");
		}
	}

	private static Page<TicketResponse> page(Page<Ticket> page) {
		return new Page<>(page.items().stream().map(TicketResponse::of).toList(), page.nextCursor());
	}

}
