package com.zawed.api.notifications;

import java.time.Instant;
import java.util.Map;
import java.util.UUID;

import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.media.Schema;
import io.swagger.v3.oas.annotations.media.Schema.RequiredMode;
import io.swagger.v3.oas.annotations.tags.Tag;

import org.springframework.http.HttpStatus;
import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.ResponseStatus;
import org.springframework.web.bind.annotation.RestController;

import com.zawed.api.notifications.Notifications.EntityType;
import com.zawed.api.notifications.Notifications.Notification;
import com.zawed.api.notifications.Notifications.Type;
import com.zawed.api.notifications.Notifications.UnreadCount;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiContract;
import com.zawed.api.platform.web.Page;

/**
 * The signed-in user's notifications, the same four endpoints for every portal. Like
 * {@code /auth/me}, they need a signed-in user and no permission: what each user sees is decided by
 * their scope and role (the service filters, row-level security filters again), and a notification
 * outside them answers 404.
 */
@RestController
@RequestMapping(ApiContract.BASE_PATH + "/notifications")
@Tag(name = "notifications")
class NotificationsController {

	record NotificationResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) Type type,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "What the notification points at") EntityType entityType,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID entityId,
			@Schema(requiredMode = RequiredMode.REQUIRED,
					description = "Ids (UUID strings), integers (piastres, millilitres) and codes (enum names) for the "
							+ "message; never display text. The keys per type are in b7-notifications-contract.md") Map<String, Object> params,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant createdAt,
			@Schema(requiredMode = RequiredMode.REQUIRED) boolean read,
			@Schema(description = "When this user read it; null while unread") Instant readAt) {

		static NotificationResponse of(Notification n) {
			return new NotificationResponse(n.id(), n.type(), n.entityType(), n.entityId(), n.params(), n.createdAt(),
					n.readAt() != null, n.readAt());
		}

	}

	record UnreadNotificationsResponse(
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "Unread notifications, at most 99") int count,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "True when there are more than 99 (show 99+)") boolean capped) {

		static UnreadNotificationsResponse of(UnreadCount c) {
			return new UnreadNotificationsResponse(c.count(), c.capped());
		}

	}

	private final Notifications notifications;

	NotificationsController(Notifications notifications) {
		this.notifications = notifications;
	}

	@GetMapping
	@Operation(operationId = "listMyNotifications", summary = "The signed-in user's notifications, newest first")
	Page<NotificationResponse> list(@AuthenticationPrincipal ZawedPrincipal principal,
			@RequestParam(required = false) @Schema(description = "Only the ones not yet read") Boolean unread,
			@RequestParam(required = false) String cursor, @RequestParam(required = false) Integer limit) {
		Page<Notification> page = notifications.mine(principal, Boolean.TRUE.equals(unread), cursor, limit);
		return new Page<>(page.items().stream().map(NotificationResponse::of).toList(), page.nextCursor());
	}

	@GetMapping("/unread-count")
	@Operation(operationId = "countMyUnreadNotifications", summary = "How many of the user's notifications are unread")
	UnreadNotificationsResponse unreadCount(@AuthenticationPrincipal ZawedPrincipal principal) {
		return UnreadNotificationsResponse.of(notifications.unread(principal));
	}

	@PostMapping("/{id}/read")
	@ResponseStatus(HttpStatus.NO_CONTENT)
	@Operation(operationId = "markMyNotificationRead", summary = "Mark one notification read; marking it again changes nothing")
	void markRead(@AuthenticationPrincipal ZawedPrincipal principal, @PathVariable UUID id) {
		notifications.markRead(principal, id);
	}

	@PostMapping("/read-all")
	@ResponseStatus(HttpStatus.NO_CONTENT)
	@Operation(operationId = "markAllMyNotificationsRead", summary = "Mark every notification the user can see read")
	void markAllRead(@AuthenticationPrincipal ZawedPrincipal principal) {
		notifications.markAllRead(principal);
	}

}
