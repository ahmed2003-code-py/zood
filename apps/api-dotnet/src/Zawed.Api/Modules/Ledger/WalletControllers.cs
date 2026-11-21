using Microsoft.AspNetCore.Mvc;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Text;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Ledger;

/// <summary>
/// The company portal's wallet (PRD §6.2 Corporate Wallet; Java <c>CompanyWalletController</c>): balance, statement and
/// top-up requests. The company is always the signed-in user's; row-level security hides every other wallet anyway.
/// </summary>
[Route("api/v1/company"), Tags("company portal")]
public sealed class CompanyWalletController(Wallets wallets, TopUps topUps, LedgerViews views) : ApiControllerBase
{
    private ZawedPrincipal Principal => ZawedPrincipal.From(User)!;

    private Guid CompanyId => Principal.Scope.Id!.Value;

    /// <summary>The company's wallet balance and low-balance state.</summary>
    [HttpGet("wallet", Name = "getMyWallet"), RequiresPermission(Permission.COMPANY_READ)]
    [ProducesResponseType<WalletResponse>(200)]
    public async Task<IActionResult> Wallet(CancellationToken ct) => From(await wallets.GetAsync(CompanyId, ct));

    /// <summary>Wallet entries, newest first, with the running balance.</summary>
    [HttpGet("wallet/statement", Name = "getMyWalletStatement"), RequiresPermission(Permission.COMPANY_READ)]
    [ProducesResponseType<Page<StatementEntryResponse>>(200)]
    public async Task<IActionResult> Statement([FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct) =>
        From(await wallets.StatementAsync(CompanyId, cursor, limit, ct));

    /// <summary>The company's top-up requests, newest first.</summary>
    [HttpGet("topups", Name = "listMyTopUps"), RequiresPermission(Permission.COMPANY_READ)]
    [ProducesResponseType<Page<TopUpResponse>>(200)]
    public async Task<IActionResult> TopUps([FromQuery] TopUpStatus? status, [FromQuery] string? cursor, [FromQuery] int? limit,
        CancellationToken ct) =>
        From(Result<Page<TopUpResponse>>.Ok(await views.TopUpsAsync(Principal, await topUps.ListAsync(CompanyId, status, cursor, limit, ct), ct)));

    /// <summary>Report a bank transfer and ask Operations to credit it.</summary>
    [HttpPost("topups", Name = "requestTopUp"), RequiresPermission(Permission.TOPUP_REQUEST)]
    [ProducesResponseType<TopUpResponse>(201)]
    public async Task<IActionResult> RequestTopUp([FromBody] TopUpRequestBody body, CancellationToken ct) =>
        From(Result<TopUpResponse>.Created(await views.TopUpAsync(Principal, await topUps.RequestAsync(Principal, CompanyId,
            body.AmountPiastres!.Value, body.BankReference, body.TransferDate!.Value, body.Note, ct), ct)));

    /// <summary>Withdraw a pending top-up request.</summary>
    [HttpPost("topups/{id}/cancel", Name = "cancelTopUp"), RequiresPermission(Permission.TOPUP_REQUEST)]
    [ProducesResponseType<TopUpResponse>(200)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct) =>
        From(Result<TopUpResponse>.Ok(await views.TopUpAsync(Principal, await topUps.CancelAsync(Principal, CompanyId, id, ct), ct)));
}

/// <summary>Wallets, the top-up queue and reconciliation in the Ops console (PRD §6.3, §6.4; Java <c>OpsWalletController</c>).</summary>
[Route("api/v1/ops"), Tags("wallets")]
public sealed class OpsWalletController(Wallets wallets, TopUps topUps, Reconciliation reconciliation, WalletAdjustments adjustments,
    LedgerViews views) : ApiControllerBase
{
    private ZawedPrincipal Actor => ZawedPrincipal.From(User)!;

    // ------------------------------------------------------------------------------------------------------ top-ups

    /// <summary>Top-up requests, newest first; filter by status and company.</summary>
    [HttpGet("topups", Name = "listTopUps"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<Page<TopUpResponse>>(200)]
    public async Task<IActionResult> List([FromQuery] TopUpStatus? status, [FromQuery] Guid? companyId, [FromQuery] string? cursor,
        [FromQuery] int? limit, CancellationToken ct) =>
        From(Result<Page<TopUpResponse>>.Ok(await views.TopUpsAsync(Actor, await topUps.ListAsync(companyId, status, cursor, limit, ct), ct)));

    /// <summary>One top-up request.</summary>
    [HttpGet("topups/{id}", Name = "getTopUp"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<TopUpResponse>(200)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        From(Result<TopUpResponse>.Ok(await views.TopUpAsync(Actor, await topUps.GetAsync(id, ct), ct)));

    /// <summary>ZAWED's bank accounts a transfer can arrive in.</summary>
    [HttpGet("topups/receiving-accounts", Name = "listReceivingAccounts"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<List<string>>(200)]
    public IActionResult ReceivingAccounts() => From(Result<IReadOnlyList<string>>.Ok(topUps.ReceivingAccounts()));

    /// <summary>Step 2: record what arrived on ZAWED's bank statement; a different person then approves.</summary>
    [HttpPost("topups/{id}/verify", Name = "verifyTopUp"), RequiresPermission(Permission.WALLET_CREDIT)]
    [ProducesResponseType<TopUpResponse>(200)]
    public async Task<IActionResult> Verify(Guid id, [FromBody] VerifyTopUpRequest body, CancellationToken ct) =>
        From(Result<TopUpResponse>.Ok(await views.TopUpAsync(Actor, await topUps.VerifyAsync(Actor, id, new Verification(body.ReceivingAccount,
            body.BankReference, body.ReceivedAmountPiastres!.Value, body.TransferDate, body.EvidenceReference, body.Note), ct), ct)));

    /// <summary>Step 3: approve a verified top-up (never one you verified); credits the verified amount once.</summary>
    [HttpPost("topups/{id}/approve", Name = "approveTopUp"), RequiresPermission(Permission.TOPUP_APPROVE)]
    [ProducesResponseType<TopUpResponse>(200)]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct) =>
        From(Result<TopUpResponse>.Ok(await views.TopUpAsync(Actor, await topUps.ApproveAsync(Actor, id, ct), ct)));

    /// <summary>Reject a top-up request with a reason.</summary>
    [HttpPost("topups/{id}/reject", Name = "rejectTopUp"), RequiresPermission(Permission.WALLET_CREDIT)]
    [ProducesResponseType<TopUpResponse>(200)]
    public async Task<IActionResult> Reject(Guid id, [FromBody] RejectTopUpRequest body, CancellationToken ct) =>
        From(Result<TopUpResponse>.Ok(await views.TopUpAsync(Actor, await topUps.RejectAsync(Actor, id, JavaText.Trim(body.Reason), ct), ct)));

    // ------------------------------------------------------------------------------------------------------ wallets

    /// <summary>A company's wallet balance and low-balance state.</summary>
    [HttpGet("companies/{id}/wallet", Name = "getCompanyWallet"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<WalletResponse>(200)]
    public async Task<IActionResult> Wallet(Guid id, CancellationToken ct) => From(await wallets.GetAsync(id, ct));

    /// <summary>A company's wallet entries, newest first.</summary>
    [HttpGet("companies/{id}/wallet/statement", Name = "getCompanyWalletStatement"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<Page<StatementEntryResponse>>(200)]
    public async Task<IActionResult> Statement(Guid id, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct) =>
        From(await wallets.StatementAsync(id, cursor, limit, ct));

    /// <summary>
    /// Record a verified bank transfer that has no company request; it waits for a second person's approval. 201 for a
    /// new record, 200 for the same record sent again (the Java document lists 200).
    /// </summary>
    [HttpPost("companies/{id}/wallet/credits", Name = "recordBankTransfer"), RequiresPermission(Permission.WALLET_CREDIT)]
    [ProducesResponseType<TopUpResponse>(200)]
    public async Task<IActionResult> Credit(Guid id, [FromBody] RecordTransferRequest body, CancellationToken ct)
    {
        var recorded = await topUps.RecordTransferAsync(Actor, id, new Verification(body.ReceivingAccount, body.BankReference,
            body.AmountPiastres!.Value, body.TransferDate, body.EvidenceReference, body.Note), ct);
        var response = await views.TopUpAsync(Actor, recorded.TopUp, ct);
        return From(recorded.Replayed ? Result<TopUpResponse>.Ok(response) : Result<TopUpResponse>.Created(response));
    }

    // -------------------------------------------------------------------------------------------------- adjustments

    /// <summary>Wallet adjustments, newest first; filter by status and company.</summary>
    [HttpGet("wallet-adjustments", Name = "listWalletAdjustments"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<Page<WalletAdjustmentResponse>>(200)]
    public async Task<IActionResult> Adjustments([FromQuery] AdjustmentStatus? status, [FromQuery] Guid? companyId, [FromQuery] string? cursor,
        [FromQuery] int? limit, CancellationToken ct) =>
        From(Result<Page<WalletAdjustmentResponse>>.Ok(await views.AdjustmentsAsync(Actor,
            await adjustments.ListAsync(companyId, status, cursor, limit, ct), ct)));

    /// <summary>
    /// Ask for a wallet credit that is not a bank transfer, with a reason; a different person approves. The
    /// Idempotency-Key is required, but bound as optional on purpose: the service refuses a missing key with its own
    /// code (400 INVALID_IDEMPOTENCY_KEY, field Idempotency-Key), after the permission. Answers 201 (the Java document
    /// lists 200).
    /// </summary>
    [HttpPost("companies/{id}/wallet/adjustments", Name = "requestWalletAdjustment"), RequiresPermission(Permission.WALLET_CREDIT)]
    [ProducesResponseType<WalletAdjustmentResponse>(200)]
    [DocumentedRequiredHeader(IdempotencyKeys.Header)]
    public async Task<IActionResult> RequestAdjustment(Guid id, [FromHeader(Name = IdempotencyKeys.Header)] string? idempotencyKey,
        [FromBody] WalletAdjustmentRequest body, CancellationToken ct) =>
        From(Result<WalletAdjustmentResponse>.Created(await views.AdjustmentAsync(Actor, await adjustments.RequestAsync(Actor, id, IdempotencyKeys.KeyOf(Request, idempotencyKey),
            body, body.AmountPiastres!.Value, body.Reason, ct), ct)));

    /// <summary>Approve an adjustment (never your own request); credits the wallet once.</summary>
    [HttpPost("wallet-adjustments/{id}/approve", Name = "approveWalletAdjustment"), RequiresPermission(Permission.TOPUP_APPROVE)]
    [ProducesResponseType<WalletAdjustmentResponse>(200)]
    public async Task<IActionResult> ApproveAdjustment(Guid id, CancellationToken ct) =>
        From(Result<WalletAdjustmentResponse>.Ok(await views.AdjustmentAsync(Actor, await adjustments.ApproveAsync(Actor, id, ct), ct)));

    /// <summary>Reject an adjustment with a reason.</summary>
    [HttpPost("wallet-adjustments/{id}/reject", Name = "rejectWalletAdjustment"), RequiresPermission(Permission.TOPUP_APPROVE)]
    [ProducesResponseType<WalletAdjustmentResponse>(200)]
    public async Task<IActionResult> RejectAdjustment(Guid id, [FromBody] RejectWalletAdjustmentRequest body, CancellationToken ct) =>
        From(Result<WalletAdjustmentResponse>.Ok(await views.AdjustmentAsync(Actor,
            await adjustments.RejectAsync(Actor, id, JavaText.Trim(body.Reason), ct), ct)));

    /// <summary>Withdraw your own adjustment request.</summary>
    [HttpPost("wallet-adjustments/{id}/cancel", Name = "cancelWalletAdjustment"), RequiresPermission(Permission.WALLET_CREDIT)]
    [ProducesResponseType<WalletAdjustmentResponse>(200)]
    public async Task<IActionResult> CancelAdjustment(Guid id, CancellationToken ct) =>
        From(Result<WalletAdjustmentResponse>.Ok(await views.AdjustmentAsync(Actor, await adjustments.CancelAsync(Actor, id, ct), ct)));

    // ----------------------------------------------------------------------------------------------- reconciliation

    /// <summary>Reconciliation runs, newest first.</summary>
    [HttpGet("reconciliation/runs", Name = "listReconciliationRuns"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<Page<ReconciliationRunResponse>>(200)]
    public async Task<IActionResult> Runs([FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct) =>
        From(await reconciliation.RunsAsync(cursor, limit, ct));

    /// <summary>Compare every cached wallet balance with its ledger now (Super Admin).</summary>
    [HttpPost("reconciliation/runs", Name = "runReconciliation"), RequiresPermission(Permission.RECONCILIATION_RUN)]
    [ProducesResponseType<ReconciliationRunResponse>(201)]
    public async Task<IActionResult> RunNow(CancellationToken ct) => From(await reconciliation.RunNowAsync(Actor, ct));

    /// <summary>A run's findings.</summary>
    [HttpGet("reconciliation/runs/{id}/findings", Name = "listReconciliationFindings"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<Page<ReconciliationFindingResponse>>(200)]
    public async Task<IActionResult> Findings(Guid id, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct) =>
        From(await reconciliation.FindingsAsync(id, cursor, limit, ct));
}

/// <summary>
/// Documents a header as required although it is bound as optional, so the service can refuse its absence with its own
/// code (Java's <c>@Parameter(required = true)</c> beside <c>@RequestHeader(required = false)</c>). Read by the
/// module's OpenAPI operation transformer.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DocumentedRequiredHeaderAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
