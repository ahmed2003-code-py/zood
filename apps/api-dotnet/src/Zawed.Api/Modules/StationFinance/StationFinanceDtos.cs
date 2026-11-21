using Zawed.Api.Modules.Identity.Contracts;
using Zawed.Api.Modules.Ledger.Contracts;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.StationFinance;

// Request and response bodies of the station finance endpoints (Java StationFinanceApi), named and shaped as the Java
// records, so the OpenAPI document and the JSON are the same. Money is integer piastres. No response carries a bank
// account number: only its kind and last four characters. A Java field marked REQUIRED is non-nullable here, every
// other one nullable.

// ------------------------------------------------------------------------------------------------------ bank accounts

public sealed record BankAccountChangeBody(
    [NotBlank, Size(max: 64)] string AccountNumber,
    [NotBlank, Size(max: 200)] string BankName,
    [NotBlank, Size(max: 200)] string HolderName,
    [NotBlank, Size(max: 100)] string BankLetterReference)
{
    /// <summary>The number must not reach a log line through a record's generated <c>ToString</c>.</summary>
    public override string ToString() => "BankAccountChangeBody[account number hidden, bankName=" + BankName + "]";
}

public sealed record RejectBankAccountChangeRequest([NotBlank, Size(max: 500)] string Reason);

public sealed record BankAccountResponse(
    Guid Id,
    Guid StationId,
    BankAccountKind AccountKind,
    string AccountLast4,
    string BankName,
    string HolderName,
    string BankLetterReference,
    BankAccountStatus Status,
    Guid? RequestedBy,
    string? RequestedByName,
    ActorRole? RequestedByRole,
    DateTimeOffset RequestedAt,
    Guid? DecidedBy,
    string? DecidedByName,
    ActorRole? DecidedByRole,
    DateTimeOffset? DecidedAt,
    string? DecisionReason,
    DateTimeOffset? PayoutsHeldUntil,
    DateTimeOffset? SupersededAt)
{
    internal static BankAccountResponse? Of(BankAccount? a, Actors people) => a is null ? null : new(a.Id, a.StationId, a.AccountKind,
        a.AccountLast4, a.BankName, a.HolderName, a.BankLetterReference, a.Status, people.Id(a.RequestedBy), people.Name(a.RequestedBy),
        people.Role(a.RequestedBy), a.RequestedAt, people.Id(a.DecidedBy), people.Name(a.DecidedBy), people.Role(a.DecidedBy), a.DecidedAt,
        a.DecisionReason, a.PayoutsHeldUntil, a.SupersededAt);
}

public sealed record StationBankDetailsResponse(BankAccountResponse? Active, BankAccountResponse? Pending)
{
    internal static StationBankDetailsResponse Of(BankDetails details, Actors people) =>
        new(BankAccountResponse.Of(details.Active, people), BankAccountResponse.Of(details.Pending, people));
}

// ------------------------------------------------------------------------------------------------------------ payouts

public sealed record RecordPayoutRequest(
    [NotNull, Positive, Max(Payouts.MaxAmountPiastres)] long? AmountPiastres,
    [NotBlank, Size(max: 64)] string BankReference,
    [NotNull] DateOnly? ValueDate,
    [Size(max: 500)] string? Note);

public sealed record CancelPayoutRequest([NotBlank, Size(max: 500)] string Reason);

public sealed record PayoutResponse(
    Guid Id,
    Guid StationId,
    Guid OperatorId,
    Guid BankAccountId,
    long AmountPiastres,
    string BankReference,
    DateOnly ValueDate,
    string? Note,
    PayoutStatus Status,
    bool ApprovalRequired,
    long ApprovalThresholdPiastres,
    Guid? RequestedBy,
    string? RequestedByName,
    ActorRole? RequestedByRole,
    DateTimeOffset RequestedAt,
    Guid? DecidedBy,
    string? DecidedByName,
    ActorRole? DecidedByRole,
    DateTimeOffset? DecidedAt,
    string? DecisionReason,
    DateTimeOffset? PostedAt,
    Guid? LedgerTransactionId)
{
    internal static PayoutResponse Of(Payout p, Actors people) => new(p.Id, p.StationId, p.OperatorId, p.BankAccountId, p.AmountPiastres,
        p.BankReference, p.ValueDate, p.Note, p.Status, p.ApprovalRequired, p.ApprovalThresholdPiastres, people.Id(p.RequestedBy),
        people.Name(p.RequestedBy), people.Role(p.RequestedBy), p.RequestedAt, people.Id(p.DecidedBy), people.Name(p.DecidedBy),
        people.Role(p.DecidedBy), p.DecidedAt, p.DecisionReason, p.PostedAt, p.LedgerTransactionId);
}

// ------------------------------------------------------------------------------------------------------ held payables

public sealed record ReleaseHoldRequest([NotBlank, Size(max: 500)] string Reason);

public sealed record HeldPayableResponse(
    Guid FuelingId,
    string Reference,
    string Plate,
    Guid StationId,
    Guid OperatorId,
    long AmountPiastres,
    DateTimeOffset EvaluatedAt,
    DateTimeOffset ReceivedAt,
    IReadOnlyList<string> Conflicts,
    HoldState State,
    Guid? ReleasedBy,
    DateTimeOffset? ReleasedAt,
    string? ReleaseReason)
{
    internal static HeldPayableResponse Of(HeldPayable h) => new(h.FuelingId, h.Reference, h.Plate, h.StationId, h.OperatorId, h.AmountPiastres,
        h.EvaluatedAt, h.ReceivedAt, h.Conflicts, h.State, h.ReleasedBy, h.ReleasedAt, h.ReleaseReason);
}

// ---------------------------------------------------------------------------------------------------------- statement

public sealed record StationPayableResponse(
    Guid StationId,
    long BalancePiastres,
    long HeldPiastres,
    long AvailablePiastres,
    long? PendingPayoutsPiastres)
{
    /// <param name="operations">Operations sees the pending payouts; the station portal reads null.</param>
    internal static StationPayableResponse Of(Guid stationId, PayablePosition p, bool operations) => new(stationId, p.BalancePiastres,
        p.HeldPiastres, p.AvailablePiastres, operations ? p.PendingPayoutsPiastres : null);
}

public sealed record StatementFuelingLink(Guid Id, string Reference, string Plate, bool PayableHeld, bool HoldReleased);

public sealed record StatementPayoutLink(Guid Id, string BankReference, DateOnly ValueDate);

public sealed record StationStatementEntryResponse(
    Guid Id,
    DateTimeOffset PostedAt,
    Direction Direction,
    long AmountPiastres,
    long BalanceAfterPiastres,
    SourceType SourceType,
    Guid SourceId,
    string? Reference,
    string? Description,
    StatementFuelingLink? Fueling,
    StatementPayoutLink? Payout)
{
    internal static StationStatementEntryResponse Of(StatementLine l) => new(l.Id, l.PostedAt, l.Direction, l.AmountPiastres,
        l.BalanceAfterPiastres, l.SourceType, l.SourceId, l.Reference, l.Description,
        l.Fueling is { } f ? new StatementFuelingLink(f.Id, f.Reference, f.Plate, f.PayableHeld, f.HoldReleased) : null,
        l.Payout is { } p ? new StatementPayoutLink(p.Id, p.BankReference, p.ValueDate) : null);

    internal static Page<StationStatementEntryResponse> PageOf(Page<StatementLine> page) => new(page.Items.Select(Of).ToList(), page.NextCursor);
}

public sealed record StationStatementSummaryResponse(
    Guid StationId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    long OpeningPiastres,
    long CreditsPiastres,
    long DebitsPiastres,
    long ClosingPiastres,
    int Entries,
    StationPayableResponse Now)
{
    internal static StationStatementSummaryResponse Of(StatementPeriod p, bool operations) => new(p.StationId, p.From, p.To, p.OpeningPiastres,
        p.CreditsPiastres, p.DebitsPiastres, p.ClosingPiastres, p.Entries, StationPayableResponse.Of(p.StationId, p.Position, operations));
}

// ---------------------------------------------------------------------------------------------------------- reversals

public sealed record RequestReversalBody([NotBlank, Size(max: 500)] string Reason);

public sealed record RejectReversalRequest([NotBlank, Size(max: 500)] string Reason);

public sealed record ReversalResponse(
    Guid Id,
    Guid FuelingId,
    string FuelingReference,
    string Plate,
    Guid CompanyId,
    Guid StationId,
    long AmountPiastres,
    string Reason,
    ReversalStatus Status,
    Guid? RequestedBy,
    DateTimeOffset RequestedAt,
    Guid? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? DecisionReason,
    Guid? LedgerTransactionId)
{
    /// <summary>As Operations reads it.</summary>
    internal static ReversalResponse Of(FuelingReversal r) => new(r.Id, r.FuelingId, r.FuelingReference, r.Plate, r.CompanyId, r.StationId,
        r.AmountPiastres, r.Reason, r.Status, r.RequestedBy, r.RequestedAt, r.DecidedBy, r.DecidedAt, r.DecisionReason, r.LedgerTransactionId);

    /// <summary>As a company or a station reads it: what happened and why, not which staff member did it.</summary>
    internal static ReversalResponse ForPortal(FuelingReversal r) => new(r.Id, r.FuelingId, r.FuelingReference, r.Plate, r.CompanyId,
        r.StationId, r.AmountPiastres, r.Reason, r.Status, null, r.RequestedAt, null, r.DecidedAt, null, r.LedgerTransactionId);

    internal static Page<ReversalResponse> PageOf(Page<FuelingReversal> page, bool portal) =>
        new(page.Items.Select(portal ? ForPortal : (Func<FuelingReversal, ReversalResponse>)Of).ToList(), page.NextCursor);
}
