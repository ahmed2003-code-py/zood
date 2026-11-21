using Zawed.Api.Modules.Ledger.Contracts;
using Zawed.Api.Modules.Tickets.Contracts;
using Zawed.Api.Platform.Events;
using Zawed.Api.Platform.Security;

namespace Zawed.Api.Modules.Notifications;

/// <summary>
/// Which event tells whom (Java <c>NotificationTriggers</c>; contract <c>docs/architecture/b7-notifications-contract.md</c>).
/// <para>Plain domain-event handlers, like the ledger's <c>CompanyCreated</c> one: each runs synchronously inside the
/// publisher's transaction, so a notification never outlives a cause that rolled back, and
/// <see cref="Notifications.SendAsync"/> requires that transaction and fails loudly without one.</para>
/// <para>The other direction is deliberately weaker (B7 W5 review, M1): a notification that cannot be written never fails
/// its cause. Each notice is built lazily inside <see cref="Notifications.SendAsync"/>'s savepoint, so even a fault in
/// building it (a bad code, a missing id) is rolled back, counted and logged there, and the fueling, top-up, adjustment,
/// ticket or payout still commits.</para>
/// </summary>
public sealed class NotificationTriggers(Notifications notifications) :
    IDomainEventHandler<WalletLowBalance>,
    IDomainEventHandler<TopUpCredited>,
    IDomainEventHandler<TopUpRejected>,
    IDomainEventHandler<TopUpAwaitingApproval>,
    IDomainEventHandler<WalletAdjustmentAwaitingApproval>,
    IDomainEventHandler<TicketAnswered>,
    IDomainEventHandler<TicketOpened>
{
    /// <summary>A company's people who watch its money and its requests.</summary>
    private static Audience Company(Guid companyId) => Audience.Of(Scope.Company(companyId), Role.FLEET_MANAGER, Role.ACCOUNTANT);

    /// <summary>An operator's Station Manager.</summary>
    private static Audience Station(Guid operatorId) => Audience.Of(Scope.Operator(operatorId), Role.STATION_MANAGER);

    /// <summary>Operations, for what someone must look at.</summary>
    private static readonly Audience Operations = Audience.Of(Scope.Platform, Role.OPS, Role.OPS_MANAGER);

    /// <summary>
    /// The checkers of money entering a wallet (ADR 0021). Super Admin also holds TOPUP_APPROVE but is not in the v1
    /// audience (brief and contract; an open question for the owner).
    /// </summary>
    private static readonly Audience Approvers = Audience.Of(Scope.Platform, Role.OPS_MANAGER);

    // ------------------------------------------------------------------------------------------------ the company

    public Task HandleAsync(WalletLowBalance e, CancellationToken ct) =>
        notifications.SendAsync(NotificationType.WALLET_LOW_BALANCE, () => new Notice(Company(e.CompanyId),
            NotificationEntityType.COMPANY_WALLET, e.CompanyId, NotificationParams.Of()
                .Number("balancePiastres", e.BalancePiastres)
                .Number("thresholdPiastres", e.ThresholdPiastres)
                .Id("transactionId", e.TransactionId)), ct);

    public Task HandleAsync(TopUpCredited e, CancellationToken ct) =>
        notifications.SendAsync(NotificationType.TOPUP_CREDITED, () => new Notice(Company(e.CompanyId), NotificationEntityType.TOPUP,
            e.TopUpId, NotificationParams.Of()
                .Number("amountPiastres", e.AmountPiastres)
                .Id("transactionId", e.TransactionId)), ct);

    /// <summary>
    /// Only a request the company made: a transfer Operations recorded and then rejected is Operations' own correction,
    /// and the company never asked for anything.
    /// </summary>
    public Task HandleAsync(TopUpRejected e, CancellationToken ct) =>
        !e.CompanyRequested
            ? Task.CompletedTask
            : notifications.SendAsync(NotificationType.TOPUP_REJECTED, () => new Notice(Company(e.CompanyId), NotificationEntityType.TOPUP,
                e.TopUpId, NotificationParams.Of().Number("amountPiastres", e.AmountPiastres)), ct);

    /// <summary>A public answer goes to the side that asked: the company's people or the station's manager.</summary>
    public Task HandleAsync(TicketAnswered e, CancellationToken ct) =>
        notifications.SendAsync(NotificationType.TICKET_ANSWERED, () => new Notice(
            e.CompanyId is { } company ? Company(company) : Station(e.OperatorId!.Value), NotificationEntityType.TICKET, e.TicketId,
            NotificationParams.Of().Code("ticketType", e.TicketType)), ct);

    // ------------------------------------------------------------------------------------------------- Operations

    public Task HandleAsync(TopUpAwaitingApproval e, CancellationToken ct) =>
        notifications.SendAsync(NotificationType.TOPUP_AWAITING_APPROVAL, () => new Notice(Approvers, NotificationEntityType.TOPUP,
            e.TopUpId, NotificationParams.Of().Id("companyId", e.CompanyId).Number("amountPiastres", e.AmountPiastres)), ct);

    public Task HandleAsync(WalletAdjustmentAwaitingApproval e, CancellationToken ct) =>
        notifications.SendAsync(NotificationType.WALLET_ADJUSTMENT_AWAITING_APPROVAL, () => new Notice(Approvers,
            NotificationEntityType.WALLET_ADJUSTMENT, e.AdjustmentId,
            NotificationParams.Of().Id("companyId", e.CompanyId).Number("amountPiastres", e.AmountPiastres)), ct);

    public Task HandleAsync(TicketOpened e, CancellationToken ct) =>
        notifications.SendAsync(NotificationType.TICKET_OPENED, () => new Notice(Operations, NotificationEntityType.TICKET, e.TicketId,
            NotificationParams.Of()
                .Code("ticketType", e.TicketType)
                .Id("companyId", e.CompanyId)
                .Id("operatorId", e.OperatorId)), ct);
}
