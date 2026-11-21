using Microsoft.AspNetCore.Mvc;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Text;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.StationFinance;

/// <summary>
/// Station finance in the Ops console (B6 contract; Java <c>OpsStationFinanceController</c>): the station's payable and
/// statement, its bank account under change control, payouts, held payables and fueling reversals. Each "ask" and each
/// "decide" has its own permission; that a person never decides their own request is a rule of the services and of the
/// database, not of this class.
/// </summary>
[Route("api/v1/ops"), Tags("station finance")]
public sealed class OpsStationFinanceController(StationStatements statements, BankAccounts bankAccounts, Payouts payouts, PayableHolds holds,
    Reversals reversals, StationFinanceViews views) : ApiControllerBase
{
    private ZawedPrincipal Actor => ZawedPrincipal.From(User)!;

    // --------------------------------------------------------------------------------------------------- statement

    /// <summary>What a station is owed, what is held and what may be paid out.</summary>
    [HttpGet("stations/{id}/payable", Name = "getStationPayable"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<StationPayableResponse>(200)]
    public async Task<IActionResult> Payable(Guid id, CancellationToken ct) =>
        Ok(StationPayableResponse.Of(id, await statements.PositionAsync(Actor, id, ct), true));

    /// <summary>A station's payable entries, newest first, with the running balance and the fueling or payout behind each.</summary>
    [HttpGet("stations/{id}/statement", Name = "getStationStatement"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<Page<StationStatementEntryResponse>>(200)]
    public async Task<IActionResult> Statement(Guid id, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct) =>
        Ok(StationStatementEntryResponse.PageOf(await statements.StatementAsync(Actor, id, cursor, limit, ct)));

    /// <summary>Opening balance, credits, debits and closing balance of a period, with the station's position now.</summary>
    [HttpGet("stations/{id}/statement/summary", Name = "getStationStatementSummary"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<StationStatementSummaryResponse>(200)]
    public async Task<IActionResult> Summary(Guid id, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken ct) =>
        Ok(StationStatementSummaryResponse.Of(await statements.PeriodAsync(Actor, id, from, to, ct), true));

    // ----------------------------------------------------------------------------------------------- bank accounts

    /// <summary>A station's verified bank account and any change that waits, masked.</summary>
    [HttpGet("stations/{id}/bank-account", Name = "getStationBankAccount"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<StationBankDetailsResponse>(200)]
    public async Task<IActionResult> BankAccount(Guid id, CancellationToken ct) =>
        NoStore(200, await views.DetailsAsync(Actor, await bankAccounts.DetailsAsync(Actor, id, ct), ct));

    /// <summary>Request a change of a station's bank account; a different person must approve it.</summary>
    [HttpPost("stations/{id}/bank-account/changes", Name = "requestBankAccountChange"), RequiresPermission(Permission.BANK_DETAILS_MANAGE)]
    [ProducesResponseType<BankAccountResponse>(200)]
    public async Task<IActionResult> RequestChange(Guid id, [FromBody] BankAccountChangeBody body, CancellationToken ct) =>
        NoStore(201, await views.BankAccountAsync(Actor, await bankAccounts.RequestChangeAsync(Actor, id, body.AccountNumber, body.BankName,
            body.HolderName, body.BankLetterReference, ct), ct));

    /// <summary>Bank account changes, newest first; filter by status, station and operator.</summary>
    [HttpGet("bank-account-changes", Name = "listBankAccountChanges"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<Page<BankAccountResponse>>(200)]
    public async Task<IActionResult> Changes([FromQuery] BankAccountStatus? status, [FromQuery] Guid? stationId, [FromQuery] Guid? operatorId,
        [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct) =>
        NoStore(200, await views.BankAccountsAsync(Actor, await bankAccounts.ChangesAsync(status, stationId, operatorId, cursor, limit, ct), ct));

    /// <summary>Approve a bank account change (never your own request); starts the payout hold.</summary>
    [HttpPost("bank-account-changes/{id}/approve", Name = "approveBankAccountChange"), RequiresPermission(Permission.BANK_DETAILS_APPROVE)]
    [ProducesResponseType<BankAccountResponse>(200)]
    public async Task<IActionResult> ApproveChange(Guid id, CancellationToken ct) =>
        NoStore(200, await views.BankAccountAsync(Actor, await bankAccounts.ApproveAsync(Actor, id, ct), ct));

    /// <summary>Reject a bank account change with a reason.</summary>
    [HttpPost("bank-account-changes/{id}/reject", Name = "rejectBankAccountChange"), RequiresPermission(Permission.BANK_DETAILS_APPROVE)]
    [ProducesResponseType<BankAccountResponse>(200)]
    public async Task<IActionResult> RejectChange(Guid id, [FromBody] RejectBankAccountChangeRequest body, CancellationToken ct) =>
        NoStore(200, await views.BankAccountAsync(Actor, await bankAccounts.RejectAsync(Actor, id, JavaText.Trim(body.Reason), ct), ct));

    // ----------------------------------------------------------------------------------------------------- payouts

    /// <summary>Payouts, newest first; filter by station, operator and status.</summary>
    [HttpGet("payouts", Name = "listPayouts"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<Page<PayoutResponse>>(200)]
    public async Task<IActionResult> List([FromQuery] Guid? stationId, [FromQuery] Guid? operatorId, [FromQuery] PayoutStatus? status,
        [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct) =>
        Ok(await views.PayoutsAsync(Actor, await payouts.ListAsync(new PayoutFilter(operatorId, stationId, status), cursor, limit, ct), ct));

    /// <summary>One payout.</summary>
    [HttpGet("payouts/{id}", Name = "getPayout"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<PayoutResponse>(200)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(await views.PayoutAsync(Actor, await payouts.GetAsync(id, ct), ct));

    /// <summary>
    /// Record a bank transfer to a station; posts at once up to the approval threshold, otherwise waits for a second
    /// person. Answers 201 (the Java document lists 200).
    /// </summary>
    [HttpPost("stations/{id}/payouts", Name = "recordPayout"), RequiresPermission(Permission.PAYOUTS_MANAGE)]
    [ProducesResponseType<PayoutResponse>(200)]
    public async Task<IActionResult> Record(Guid id, [FromBody] RecordPayoutRequest body, CancellationToken ct) =>
        StatusCode(201, await views.PayoutAsync(Actor, await payouts.RecordAsync(Actor, id, body.AmountPiastres!.Value, body.BankReference,
            body.ValueDate!.Value, body.Note, ct), ct));

    /// <summary>Approve a payout that waits (never one you recorded); posts it.</summary>
    [HttpPost("payouts/{id}/approve", Name = "approvePayout"), RequiresPermission(Permission.PAYOUTS_APPROVE)]
    [ProducesResponseType<PayoutResponse>(200)]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct) =>
        Ok(await views.PayoutAsync(Actor, await payouts.ApproveAsync(Actor, id, ct), ct));

    /// <summary>Withdraw a payout that still waits for approval.</summary>
    [HttpPost("payouts/{id}/cancel", Name = "cancelPayout"), RequiresPermission(Permission.PAYOUTS_MANAGE)]
    [ProducesResponseType<PayoutResponse>(200)]
    public async Task<IActionResult> Cancel(Guid id, [FromBody] CancelPayoutRequest body, CancellationToken ct) =>
        Ok(await views.PayoutAsync(Actor, await payouts.CancelAsync(Actor, id, JavaText.Trim(body.Reason), ct), ct));

    // ----------------------------------------------------------------------------------------------- held payables

    /// <summary>Fuelings whose payable was held at sync, newest first; filter by station, operator and state.</summary>
    [HttpGet("payable-holds", Name = "listPayableHolds"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<Page<HeldPayableResponse>>(200)]
    public async Task<IActionResult> Holds([FromQuery] Guid? stationId, [FromQuery] Guid? operatorId, [FromQuery] HoldState? state,
        [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct)
    {
        var page = await holds.ListAsync(operatorId, stationId, state, cursor, limit, ct);
        return Ok(new Page<HeldPayableResponse>(page.Items.Select(HeldPayableResponse.Of).ToList(), page.NextCursor));
    }

    /// <summary>Release a held payable with a reason; it then counts as available.</summary>
    [HttpPost("fuelings/{id}/payable-hold/release", Name = "releasePayableHold"), RequiresPermission(Permission.PAYABLE_HOLD_RELEASE)]
    [ProducesResponseType<HeldPayableResponse>(200)]
    public async Task<IActionResult> Release(Guid id, [FromBody] ReleaseHoldRequest body, CancellationToken ct) =>
        Ok(HeldPayableResponse.Of(await holds.ReleaseAsync(Actor, id, JavaText.Trim(body.Reason), ct)));

    // --------------------------------------------------------------------------------------------------- reversals

    /// <summary>
    /// Request the reversal of a completed fueling; a different person must approve it. Answers 201 (the Java document
    /// lists 200).
    /// </summary>
    [HttpPost("fuelings/{id}/reversals", Name = "requestFuelingReversal"), RequiresPermission(Permission.REVERSALS_REQUEST)]
    [ProducesResponseType<ReversalResponse>(200)]
    public async Task<IActionResult> RequestReversal(Guid id, [FromBody] RequestReversalBody body, CancellationToken ct) =>
        StatusCode(201, ReversalResponse.Of(await reversals.RequestAsync(Actor, id, JavaText.Trim(body.Reason), ct)));

    /// <summary>Fueling reversals, newest first; filter by status, company, station and fueling.</summary>
    [HttpGet("reversals", Name = "listReversals"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<Page<ReversalResponse>>(200)]
    public async Task<IActionResult> Reversals([FromQuery] ReversalStatus? status, [FromQuery] Guid? companyId, [FromQuery] Guid? operatorId,
        [FromQuery] Guid? stationId, [FromQuery] Guid? fuelingId, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct) =>
        Ok(ReversalResponse.PageOf(await reversals.ListAsync(new ReversalFilter(companyId, operatorId, stationId, fuelingId, status), cursor,
            limit, ct), portal: false));

    /// <summary>One fueling reversal.</summary>
    [HttpGet("reversals/{id}", Name = "getReversal"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<ReversalResponse>(200)]
    public async Task<IActionResult> Reversal(Guid id, CancellationToken ct) => Ok(ReversalResponse.Of(await reversals.GetAsync(id, ct)));

    /// <summary>Approve a reversal (never your own request); posts the mirror of the fueling.</summary>
    [HttpPost("reversals/{id}/approve", Name = "approveReversal"), RequiresPermission(Permission.REVERSALS_APPROVE)]
    [ProducesResponseType<ReversalResponse>(200)]
    public async Task<IActionResult> ApproveReversal(Guid id, CancellationToken ct) =>
        Ok(ReversalResponse.Of(await reversals.ApproveAsync(Actor, id, ct)));

    /// <summary>Reject a reversal request with a reason.</summary>
    [HttpPost("reversals/{id}/reject", Name = "rejectReversal"), RequiresPermission(Permission.REVERSALS_APPROVE)]
    [ProducesResponseType<ReversalResponse>(200)]
    public async Task<IActionResult> RejectReversal(Guid id, [FromBody] RejectReversalRequest body, CancellationToken ct) =>
        Ok(ReversalResponse.Of(await reversals.RejectAsync(Actor, id, JavaText.Trim(body.Reason), ct)));

    /// <summary>Bank details are not for a shared cache or a browser's history, even masked (Java <c>CacheControl.noStore()</c>).</summary>
    private ObjectResult NoStore<T>(int status, T body)
    {
        Response.Headers.CacheControl = "no-store";
        return StatusCode(status, body);
    }
}

/// <summary>
/// Station finance as the station manager and the company read it (B6 contract §6; Java
/// <c>PortalStationFinanceController</c>). Read only: every change happens at Operations. The scope comes from the
/// signed-in user, never from the request (<see cref="StationAccess"/>), and row-level security limits what each side
/// sees: posted payouts and approved reversals only, and never which staff member asked or decided.
/// </summary>
[Route("api/v1")]
public sealed class PortalStationFinanceController(StationStatements statements, BankAccounts bankAccounts, Payouts payouts,
    Reversals reversals, StationFinanceViews views) : ApiControllerBase
{
    private ZawedPrincipal Principal => ZawedPrincipal.From(User)!;

    // ----------------------------------------------------------------------------------------------------- station

    /// <summary>What one of the operator's stations is owed, what is held and what is available.</summary>
    [HttpGet("station/stations/{id}/payable", Name = "getMyStationPayable"), Tags("station portal"), RequiresPermission(Permission.STATION_READ)]
    [ProducesResponseType<StationPayableResponse>(200)]
    public async Task<IActionResult> Payable(Guid id, CancellationToken ct) =>
        Ok(StationPayableResponse.Of(id, await statements.PositionAsync(Principal, id, ct), false));

    /// <summary>The station's payable entries, newest first, with the running balance and the fueling or payout behind each.</summary>
    [HttpGet("station/stations/{id}/statement", Name = "getMyStationStatement"), Tags("station portal"),
     RequiresPermission(Permission.STATION_READ)]
    [ProducesResponseType<Page<StationStatementEntryResponse>>(200)]
    public async Task<IActionResult> Statement(Guid id, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct) =>
        Ok(StationStatementEntryResponse.PageOf(await statements.StatementAsync(Principal, id, cursor, limit, ct)));

    /// <summary>Opening balance, credits, debits and closing balance of a period.</summary>
    [HttpGet("station/stations/{id}/statement/summary", Name = "getMyStationStatementSummary"), Tags("station portal"),
     RequiresPermission(Permission.STATION_READ)]
    [ProducesResponseType<StationStatementSummaryResponse>(200)]
    public async Task<IActionResult> Summary(Guid id, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken ct) =>
        Ok(StationStatementSummaryResponse.Of(await statements.PeriodAsync(Principal, id, from, to, ct), false));

    /// <summary>Where the station is paid, masked, and any change that waits for approval.</summary>
    [HttpGet("station/stations/{id}/bank-account", Name = "getMyStationBankAccount"), Tags("station portal"),
     RequiresPermission(Permission.STATION_READ)]
    [ProducesResponseType<StationBankDetailsResponse>(200)]
    public async Task<IActionResult> BankAccount(Guid id, CancellationToken ct)
    {
        var details = await views.DetailsAsync(Principal, await bankAccounts.DetailsAsync(Principal, id, ct), ct);
        Response.Headers.CacheControl = "no-store";
        return Ok(details);
    }

    /// <summary>Payouts the operator's stations received, newest first.</summary>
    [HttpGet("station/payouts", Name = "listMyPayouts"), Tags("station portal"), RequiresPermission(Permission.STATION_READ)]
    [ProducesResponseType<Page<PayoutResponse>>(200)]
    public async Task<IActionResult> Payouts([FromQuery] Guid? stationId, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct)
    {
        var filter = new PayoutFilter(StationAccess.OperatorFilter(Principal, null), stationId, PayoutStatus.POSTED);
        return Ok(await views.PayoutsAsync(Principal, await payouts.ListAsync(filter, cursor, limit, ct), ct));
    }

    /// <summary>Approved reversals of fuelings at the operator's stations.</summary>
    [HttpGet("station/reversals", Name = "listStationReversals"), Tags("station portal"), RequiresPermission(Permission.STATION_READ)]
    [ProducesResponseType<Page<ReversalResponse>>(200)]
    public async Task<IActionResult> StationReversals([FromQuery] Guid? stationId, [FromQuery] string? cursor, [FromQuery] int? limit,
        CancellationToken ct)
    {
        var filter = new ReversalFilter(null, StationAccess.OperatorFilter(Principal, null), stationId, null, ReversalStatus.APPROVED);
        return Ok(ReversalResponse.PageOf(await reversals.ListAsync(filter, cursor, limit, ct), portal: true));
    }

    // ----------------------------------------------------------------------------------------------------- company

    /// <summary>Approved reversals of the company's fuelings, refunded to its wallet.</summary>
    [HttpGet("company/reversals", Name = "listMyReversals"), Tags("company portal"), RequiresPermission(Permission.COMPANY_READ)]
    [ProducesResponseType<Page<ReversalResponse>>(200)]
    public async Task<IActionResult> CompanyReversals([FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct)
    {
        var filter = new ReversalFilter(Principal.Scope.Id, null, null, null, ReversalStatus.APPROVED);
        return Ok(ReversalResponse.PageOf(await reversals.ListAsync(filter, cursor, limit, ct), portal: true));
    }
}
