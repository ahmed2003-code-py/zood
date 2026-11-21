namespace Zawed.Api.Modules.StationFinance.Contracts;

// Station finance's domain events, published synchronously inside the transaction that caused them (IDomainEvents), as
// the Java ApplicationEventPublisher did. The notifications (3C, wave 5) register IDomainEventHandler<T> for them; a
// handler that must not fail its cause wraps its write in a savepoint.

/// <summary>
/// A payout has been posted to the ledger (Java <c>stationfinance.PayoutPosted</c>): at once when it is at or below the
/// approval threshold, or when a second person approves it. A payout waiting for approval publishes nothing. Published
/// inside the posting transaction.
/// </summary>
public sealed record PayoutPosted(Guid PayoutId, Guid OperatorId, Guid StationId, long AmountPiastres);

/// <summary>
/// A station's bank account change was approved or rejected (Java <c>stationfinance.BankAccountChangeDecided</c>). The
/// account number, its last digits and the reason are not part of it. Published inside the deciding transaction.
/// </summary>
public sealed record BankAccountChangeDecided(Guid ChangeId, Guid OperatorId, Guid StationId, bool Approved);
