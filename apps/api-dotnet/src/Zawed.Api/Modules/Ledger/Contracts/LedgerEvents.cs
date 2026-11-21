namespace Zawed.Api.Modules.Ledger.Contracts;

// The ledger's domain events, published synchronously inside the transaction that caused them (IDomainEvents), as
// the Java ApplicationEventPublisher did. Nothing handles them yet: the notifications (wave 3) and the alerts (B4)
// register IDomainEventHandler<T> for them. A handler that must not fail its cause wraps its write in a savepoint.

/// <summary>
/// A posting took a company wallet from at-or-above its low-balance threshold to below it (PRD §4.7). The same fact
/// is written to the audit log as <c>WALLET_LOW_BALANCE</c>.
/// </summary>
public sealed record WalletLowBalance(Guid CompanyId, long BalancePiastres, long ThresholdPiastres, Guid TransactionId);

/// <summary>
/// A top-up reached <c>PENDING_APPROVAL</c>: an Operations user verified a company's transfer, or recorded one the
/// company never announced (ADR 0021). A second person holding <c>TOPUP_APPROVE</c> must now approve it. Not published
/// again when a recorded transfer is sent a second time and answers the existing record.
/// </summary>
/// <param name="AmountPiastres">the verified amount, the one that will be credited</param>
public sealed record TopUpAwaitingApproval(Guid TopUpId, Guid CompanyId, long AmountPiastres);

/// <summary>
/// An approved top-up has credited the company wallet. Exactly once per top-up: a repeated approval posts nothing and
/// publishes nothing.
/// </summary>
/// <param name="AmountPiastres">what the wallet received (the verified amount)</param>
public sealed record TopUpCredited(Guid TopUpId, Guid CompanyId, long AmountPiastres, Guid TransactionId);

/// <summary>Operations rejected a top-up, at either step. The reason stays in the top-up row and the audit log.</summary>
/// <param name="CompanyRequested">true when the company asked for it, false for a transfer Operations recorded itself</param>
/// <param name="AmountPiastres">the amount the company claimed (or Operations recorded)</param>
public sealed record TopUpRejected(Guid TopUpId, Guid CompanyId, bool CompanyRequested, long AmountPiastres);

/// <summary>
/// An Operations user asked for a wallet adjustment (V12, ADR 0021). A different person holding <c>TOPUP_APPROVE</c>
/// must approve it. A repeat of the same request under its Idempotency-Key publishes nothing.
/// </summary>
public sealed record WalletAdjustmentAwaitingApproval(Guid AdjustmentId, Guid CompanyId, long AmountPiastres);
