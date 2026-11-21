using Zawed.Api.Modules.Identity.Contracts;
using Zawed.Api.Modules.Ledger.Contracts;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Ledger;

// Request and response bodies of the wallet, top-up and reconciliation endpoints (Java LedgerApi), named and shaped
// as the Java records, so the OpenAPI document and the JSON are the same. Money is integer piastres.

public enum TopUpStatus
{
    PENDING,
    PENDING_APPROVAL,
    CONFIRMED,
    REJECTED,
    CANCELLED,
}

public enum TopUpOrigin
{
    COMPANY_REQUEST,
    OPS_RECORDED,
}

public enum AdjustmentStatus
{
    PENDING_APPROVAL,
    APPROVED,
    REJECTED,
    CANCELLED,
}

public enum ReconciliationTrigger
{
    SCHEDULED,
    MANUAL,
}

public enum FindingKind
{
    /// <summary>A wallet's cached balance differs from the ledger; the ledger is the truth.</summary>
    BALANCE_MISMATCH,

    /// <summary>A company exists without a wallet row.</summary>
    MISSING_WALLET,

    /// <summary>A station payable's cached balance differs from the ledger; the ledger is the truth.</summary>
    PAYABLE_MISMATCH,

    /// <summary>A station payable account exists without its cached row.</summary>
    MISSING_PAYABLE,
}

public static class LedgerLimits
{
    /// <summary>A sanity cap on one top-up: 1,000,000,000 EGP. Above it is a typo, not a transfer.</summary>
    public const long MaxAmountPiastres = 100_000_000_000L;
}

public sealed record TopUpRequestBody(
    [NotNull, Positive, Max(LedgerLimits.MaxAmountPiastres)] long? AmountPiastres,
    [NotBlank, Size(max: 64)] string BankReference,
    [NotNull] DateOnly? TransferDate,
    [Size(max: 500)] string? Note);

/// <summary>Step 2 of a top-up (V12): what Operations read on ZAWED's bank statement.</summary>
public sealed record VerifyTopUpRequest(
    [NotNull, Positive, Max(LedgerLimits.MaxAmountPiastres)] long? ReceivedAmountPiastres,
    [Size(max: 32)] string? ReceivingAccount,
    [Size(max: 64)] string? BankReference,
    DateOnly? TransferDate,
    [Size(max: 200)] string? EvidenceReference,
    [Size(max: 500)] string? Note);

/// <summary>A transfer that arrived without a company request, recorded and verified by Operations (V12).</summary>
public sealed record RecordTransferRequest(
    [NotNull, Positive, Max(LedgerLimits.MaxAmountPiastres)] long? AmountPiastres,
    [Size(max: 32)] string? ReceivingAccount,
    [NotBlank, Size(max: 64)] string BankReference,
    [NotNull] DateOnly? TransferDate,
    [Size(max: 200)] string? EvidenceReference,
    [Size(max: 500)] string? Note);

public sealed record WalletAdjustmentRequest(
    [NotNull, Positive, Max(LedgerLimits.MaxAmountPiastres)] long? AmountPiastres,
    [NotBlank, Size(max: 500)] string Reason);

public sealed record RejectWalletAdjustmentRequest([NotBlank, Size(max: 500)] string Reason);

public sealed record RejectTopUpRequest([NotBlank, Size(max: 500)] string Reason);

public sealed record WalletResponse(Guid CompanyId, long BalancePiastres, long LowBalanceThresholdPiastres, bool LowBalance,
    DateTimeOffset UpdatedAt);

public sealed record StatementEntryResponse(Guid Id, DateTimeOffset PostedAt, Direction Direction, long AmountPiastres,
    long BalanceAfterPiastres, SourceType SourceType, Guid SourceId, string? Reference, string? Description);

public sealed record TopUpResponse(
    Guid Id,
    Guid CompanyId,
    TopUpOrigin Origin,
    long AmountPiastres,
    string ReceivingAccount,
    string? ReceivingAccountLabel,
    string BankReference,
    DateOnly TransferDate,
    string? Note,
    TopUpStatus Status,
    Guid? RequestedBy,
    string? RequestedByName,
    ActorRole? RequestedByRole,
    DateTimeOffset RequestedAt,
    long? VerifiedAmountPiastres,
    Guid? VerifiedBy,
    string? VerifiedByName,
    ActorRole? VerifiedByRole,
    DateTimeOffset? VerifiedAt,
    string? EvidenceReference,
    Guid? DecidedBy,
    string? DecidedByName,
    ActorRole? DecidedByRole,
    DateTimeOffset? DecidedAt,
    string? DecisionReason,
    Guid? LedgerTransactionId)
{
    internal static TopUpResponse Of(TopUp t, Actors people, string? receivingAccountLabel) => new(t.Id, t.CompanyId, t.Origin,
        t.AmountPiastres, t.ReceivingAccount, receivingAccountLabel, t.BankReference, t.TransferDate, t.Note, t.Status,
        people.Id(t.RequestedBy), people.Name(t.RequestedBy), people.Role(t.RequestedBy), t.RequestedAt, t.VerifiedAmountPiastres,
        people.Id(t.VerifiedBy), people.Name(t.VerifiedBy), people.Role(t.VerifiedBy), t.VerifiedAt, t.EvidenceReference,
        people.Id(t.DecidedBy), people.Name(t.DecidedBy), people.Role(t.DecidedBy), t.DecidedAt, t.DecisionReason, t.LedgerTransactionId);
}

public sealed record WalletAdjustmentResponse(
    Guid Id,
    Guid CompanyId,
    long AmountPiastres,
    string Reason,
    AdjustmentStatus Status,
    Guid RequestedBy,
    string? RequestedByName,
    ActorRole? RequestedByRole,
    DateTimeOffset RequestedAt,
    Guid? DecidedBy,
    string? DecidedByName,
    ActorRole? DecidedByRole,
    DateTimeOffset? DecidedAt,
    string? DecisionReason,
    Guid? LedgerTransactionId)
{
    // Java passes people.id(a.requestedBy()) into a field the document marks required; a requester is always an Ops
    // user, so a platform viewer (the only one) always gets the id back.
    internal static WalletAdjustmentResponse Of(Adjustment a, Actors people) => new(a.Id, a.CompanyId, a.AmountPiastres, a.Reason,
        a.Status, people.Id(a.RequestedBy) ?? a.RequestedBy, people.Name(a.RequestedBy), people.Role(a.RequestedBy), a.RequestedAt,
        people.Id(a.DecidedBy), people.Name(a.DecidedBy), people.Role(a.DecidedBy), a.DecidedAt, a.DecisionReason, a.LedgerTransactionId);
}

public sealed record ReconciliationRunResponse(Guid Id, ReconciliationTrigger Trigger, Guid? TriggeredBy, DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt, int WalletsChecked, int PayablesChecked, int FindingsCount);

public sealed record ReconciliationFindingResponse(Guid Id, Guid RunId, Guid? CompanyId, Guid? StationId, FindingKind Kind,
    long? CachedBalancePiastres, long? LedgerBalancePiastres, long? DifferencePiastres, DateTimeOffset CreatedAt);
