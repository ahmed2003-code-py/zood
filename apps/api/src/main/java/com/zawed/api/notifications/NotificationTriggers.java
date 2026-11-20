package com.zawed.api.notifications;

import java.util.UUID;

import org.springframework.context.event.EventListener;
import org.springframework.stereotype.Component;

import com.zawed.api.fueling.HardAlertRaised;
import com.zawed.api.ledger.TopUpAwaitingApproval;
import com.zawed.api.ledger.TopUpCredited;
import com.zawed.api.ledger.TopUpRejected;
import com.zawed.api.ledger.WalletAdjustmentAwaitingApproval;
import com.zawed.api.ledger.WalletLowBalance;
import com.zawed.api.notifications.Notifications.Audience;
import com.zawed.api.notifications.Notifications.EntityType;
import com.zawed.api.notifications.Notifications.Notice;
import com.zawed.api.notifications.Notifications.Params;
import com.zawed.api.notifications.Notifications.Type;
import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.stationfinance.BankAccountChangeDecided;
import com.zawed.api.stationfinance.PayoutPosted;
import com.zawed.api.tickets.TicketAnswered;
import com.zawed.api.tickets.TicketOpened;

/**
 * Which event tells whom (contract: {@code docs/architecture/b7-notifications-contract.md}).
 *
 * <p>Plain {@code @EventListener}s, like {@code fueling.Alerts} and {@code ledger.Wallets}: each runs
 * synchronously inside the publisher's transaction, so a notification never outlives a cause that
 * rolled back. Chosen over {@code @TransactionalEventListener(BEFORE_COMMIT)}, which would also write
 * in the same transaction, because a before-commit listener is silently skipped when there is no
 * transaction and would outlive a rolled-back savepoint; here {@link Notifications#send} requires the
 * transaction ({@code MANDATORY}) and fails loudly instead.
 *
 * <p>The other direction is deliberately weaker (B7 W5 review, M1): a notification that cannot be
 * written never fails its cause. Each notice is built lazily inside {@link Notifications#send}'s
 * savepoint, so even a fault in building it (a bad code, a missing id) is rolled back, counted and
 * logged there, and the fueling, top-up, adjustment, ticket or payout still commits.
 */
@Component
class NotificationTriggers {

	/** A company's people who watch its money and its requests. */
	private static Audience company(UUID companyId) {
		return Audience.of(Scope.company(companyId), Role.FLEET_MANAGER, Role.ACCOUNTANT);
	}

	/** An operator's Station Manager. */
	private static Audience station(UUID operatorId) {
		return Audience.of(Scope.operator(operatorId), Role.STATION_MANAGER);
	}

	/** Operations, for what someone must look at. */
	private static final Audience OPERATIONS = Audience.of(Scope.PLATFORM, Role.OPS, Role.OPS_MANAGER);

	/**
	 * The checkers of money entering a wallet (ADR 0021). Super Admin also holds TOPUP_APPROVE but is
	 * not in the v1 audience (brief and contract; an open question for the owner).
	 */
	private static final Audience APPROVERS = Audience.of(Scope.PLATFORM, Role.OPS_MANAGER);

	private final Notifications notifications;

	NotificationTriggers(Notifications notifications) {
		this.notifications = notifications;
	}

	// ------------------------------------------------------------------------------- the company

	@EventListener
	void on(WalletLowBalance event) {
		notifications.send(Type.WALLET_LOW_BALANCE,
				() -> new Notice(company(event.companyId()), EntityType.COMPANY_WALLET, event.companyId(),
						Params.of()
							.number("balancePiastres", event.balancePiastres())
							.number("thresholdPiastres", event.thresholdPiastres())
							.id("transactionId", event.transactionId())));
	}

	@EventListener
	void on(TopUpCredited event) {
		notifications.send(Type.TOPUP_CREDITED,
				() -> new Notice(company(event.companyId()), EntityType.TOPUP, event.topUpId(), Params.of()
					.number("amountPiastres", event.amountPiastres())
					.id("transactionId", event.transactionId())));
	}

	/**
	 * Only a request the company made: a transfer Operations recorded and then rejected is Operations'
	 * own correction, and the company never asked for anything.
	 */
	@EventListener
	void on(TopUpRejected event) {
		if (!event.companyRequested()) {
			return;
		}
		notifications.send(Type.TOPUP_REJECTED, () -> new Notice(company(event.companyId()), EntityType.TOPUP,
				event.topUpId(), Params.of().number("amountPiastres", event.amountPiastres())));
	}

	/** A public answer goes to the side that asked: the company's people or the station's manager. */
	@EventListener
	void on(TicketAnswered event) {
		notifications.send(Type.TICKET_ANSWERED,
				() -> new Notice(event.companyId() != null ? company(event.companyId()) : station(event.operatorId()),
						EntityType.TICKET, event.ticketId(), Params.of().code("ticketType", event.ticketType())));
	}

	// --------------------------------------------------------------------------------- Operations

	@EventListener
	void on(TopUpAwaitingApproval event) {
		notifications.send(Type.TOPUP_AWAITING_APPROVAL,
				() -> new Notice(APPROVERS, EntityType.TOPUP, event.topUpId(),
						Params.of().id("companyId", event.companyId()).number("amountPiastres", event.amountPiastres())));
	}

	@EventListener
	void on(WalletAdjustmentAwaitingApproval event) {
		notifications.send(Type.WALLET_ADJUSTMENT_AWAITING_APPROVAL,
				() -> new Notice(APPROVERS, EntityType.WALLET_ADJUSTMENT, event.adjustmentId(),
						Params.of().id("companyId", event.companyId()).number("amountPiastres", event.amountPiastres())));
	}

	@EventListener
	void on(HardAlertRaised event) {
		notifications.send(Type.HARD_ALERT_RAISED,
				() -> new Notice(OPERATIONS, EntityType.ALERT, event.alertId(),
						Params.of()
							.code("alertType", event.type())
							.code("severity", event.severity())
							.id("companyId", event.companyId())
							.id("operatorId", event.operatorId())
							.id("stationId", event.stationId())
							.id("fuelingId", event.fuelingId())));
	}

	@EventListener
	void on(TicketOpened event) {
		notifications.send(Type.TICKET_OPENED,
				() -> new Notice(OPERATIONS, EntityType.TICKET, event.ticketId(),
						Params.of()
							.code("ticketType", event.ticketType())
							.id("companyId", event.companyId())
							.id("operatorId", event.operatorId())));
	}

	// ---------------------------------------------------------------------------------- the station

	@EventListener
	void on(PayoutPosted event) {
		notifications.send(Type.PAYOUT_POSTED,
				() -> new Notice(station(event.operatorId()), EntityType.PAYOUT, event.payoutId(),
						Params.of().id("stationId", event.stationId()).number("amountPiastres", event.amountPiastres())));
	}

	@EventListener
	void on(BankAccountChangeDecided event) {
		notifications.send(event.approved() ? Type.BANK_ACCOUNT_CHANGE_APPROVED : Type.BANK_ACCOUNT_CHANGE_REJECTED,
				() -> new Notice(station(event.operatorId()), EntityType.STATION_BANK_ACCOUNT, event.changeId(),
						Params.of().id("stationId", event.stationId())));
	}

}
