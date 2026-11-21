using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Zawed.Api.Platform.Web;
using Zawed.Domain;
using Zawed.Domain.Rules;

namespace Zawed.Api.Modules.Fueling;

// Request and response bodies of the fueling endpoints, named as the Java records (Java FuelingApi) so the OpenAPI
// document and the JSON are the same.

public sealed record PosFuelingRequest(
    [NotNull] Guid? ClientTxnId,
    [NotBlank, Size(max: 64)] string TagUid,
    [NotNull, Positive] long? VolumeMl,
    FuelType? FuelType,
    [NotNull] DateTimeOffset? DeviceAt,
    [NotNull, Pattern("ar|en")] string? ReceiptLocale)
{
    public FuelingRequest ToRequest() => new(ClientTxnId!.Value, TagUid, VolumeMl!.Value, FuelType, DeviceAt!.Value, ReceiptLocale!);
}

/// <summary>What the POS shows and prints. The wallet is a status word, never an amount (PRD §6.1).</summary>
public sealed record PosFuelingResponse(Guid Id, string Reference, FuelingStatus Status, FuelingRules.DeclineReason? DeclineReason,
    bool Replayed, string Plate, string CompanyNameAr, string CompanyNameEn, string StationNameAr, string StationNameEn, string AgentWorkerId,
    FuelType FuelType, long VolumeMl, long UnitPricePiastres, long AmountPiastres, bool Billable, DateTimeOffset EvaluatedAt,
    WalletStatus? WalletStatus, DateTimeOffset? CooldownUntil, string ReceiptLocale, string ReceiptPath)
{
    public static PosFuelingResponse Of(FuelingOutcome result, string receiptPath)
    {
        var f = result.Fueling;
        return new(f.Id, f.Reference, f.Status, f.DeclineReason, result.Replayed, f.Plate, f.CompanyNameAr, f.CompanyNameEn, f.StationNameAr,
            f.StationNameEn, f.AgentWorkerId, f.FuelType, f.VolumeMl, f.UnitPricePiastres, f.AmountPiastres, f.Billable, f.EvaluatedAt,
            result.WalletStatus, result.CooldownUntil, f.ReceiptLocale, receiptPath);
    }
}

// ---------------------------------------------------------------------------------------------------------------- sync

/// <summary>One fueling the device captured while offline (B5 contract §3; Java <c>FuelingApi.PosSyncItem</c>).</summary>
/// <param name="VolumeMl">
/// A number, not a <c>long</c>: one item's fraction must not refuse the whole batch (B5 §3). An integer written as a float
/// (<c>20000.0</c>) is that integer; a real fraction is this item's <c>INVALID_VOLUME</c>.
/// </param>
/// <param name="UnitPricePiastres">the price per litre the terminal showed; a fraction rejects the item with VALIDATION_FAILED</param>
public sealed record PosSyncItem(
    [NotNull] Guid? ClientTxnId,
    [NotBlank, Size(max: 64)] string TagUid,
    [NotNull] SyncNumber? VolumeMl,
    FuelType? FuelType,
    [NotNull] DateTimeOffset? DeviceAt,
    [NotNull] Guid? AgentId,
    [NotNull, Pattern("ar|en")] string? ReceiptLocale,
    SyncNumber? UnitPricePiastres)
{
    public OfflineRequest ToRequest()
    {
        var volume = VolumeMl!.Value.Whole;
        var price = UnitPricePiastres?.Whole;
        var notWhole = volume is null ? new NotWhole("volumeMl", VolumeMl.Value.ToString())
            : UnitPricePiastres is { } sent && price is null ? new NotWhole("unitPricePiastres", sent.ToString())
            : null;
        return new OfflineRequest(ClientTxnId!.Value, TagUid, volume ?? 0, FuelType, DeviceAt!.Value, AgentId!.Value, ReceiptLocale!, price, notWhole);
    }
}

/// <summary>A batch of offline captures (Java <c>FuelingApi.PosSyncRequest</c>).</summary>
/// <param name="BatchId">the device's own id for this batch, shown in the sync monitor; sending a batch again is safe</param>
/// <param name="Pending">how many captures the device still holds after this batch</param>
/// <param name="Items">at most 100, in capture order</param>
public sealed record PosSyncRequest(
    [NotNull] Guid? BatchId,
    [NotNull, PositiveOrZero] int? Pending,
    [NotNull] IReadOnlyList<PosSyncItem>? Items) : IValidatableObject
{
    public OfflineBatch ToBatch() => new(BatchId!.Value, Pending!.Value, Items!.Select(i => i.ToRequest()).ToList());

    /// <summary>Java's <c>List&lt;@NotNull @Valid PosSyncItem&gt;</c>: a null item is the terminal's fault, 400 for the request.</summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        for (var i = 0; i < (Items?.Count ?? 0); i++)
        {
            if (Items![i] is null)
            {
                yield return new ValidationResult(Platform.Web.Validation.Encode("NOT_NULL", "must not be null"), [$"Items[{i}]"]);
            }
        }
    }
}

/// <summary>What the device needs back about a fueling the server holds: enough to show it, never a balance.</summary>
/// <param name="Conflicts">checks that failed when the server judged the capture; it is recorded all the same</param>
/// <param name="ReceiptPath">path of the signed receipt link, under the API base</param>
public sealed record PosSyncFueling(Guid Id, string Reference, FuelingStatus Status, FuelingOrigin Origin, string Plate, FuelType FuelType,
    long VolumeMl, long UnitPricePiastres, long AmountPiastres, bool Billable, IReadOnlyList<Conflict> Conflicts, DateTimeOffset EvaluatedAt,
    string ReceiptPath)
{
    public static PosSyncFueling Of(Fueling f, string receiptPath) => new(f.Id, f.Reference, f.Status, f.Origin, f.Plate, f.FuelType,
        f.VolumeMl, f.UnitPricePiastres, f.AmountPiastres, f.Billable, f.Conflicts, f.EvaluatedAt, receiptPath);
}

/// <param name="Code">
/// why the item was rejected: TAG_NOT_RECOGNISED, AGENT_NOT_OF_STATION, CLIENT_TXN_CONFLICT, IDEMPOTENCY_MISMATCH,
/// INVALID_VOLUME or VALIDATION_FAILED; for RETRY, the server fault
/// </param>
/// <param name="Fueling">the stored fueling, when recorded or duplicate</param>
public sealed record PosSyncItemResult(Guid ClientTxnId, SyncOutcome Result, string? Code, PosSyncFueling? Fueling);

/// <param name="Results">one result per item, in the order the items were sent</param>
public sealed record PosSyncResponse(Guid BatchId, IReadOnlyList<PosSyncItemResult> Results);

/// <summary>What the agent was shown while the answer to an online fueling was lost (B5 review 6).</summary>
public sealed record ResolveRequest([NotNull, Pattern("REFUSED|COMPLETED")] string? ToldOutcome);

public sealed record FuelingResponse(Guid Id, string Reference, FuelingStatus Status, FuelingRules.DeclineReason? DeclineReason,
    IReadOnlyList<Conflict> Conflicts, FuelingOrigin Origin, Guid CompanyId, string CompanyNameAr, string CompanyNameEn, Guid VehicleId,
    string Plate, Guid OperatorId, Guid StationId, string StationNameAr, string StationNameEn, Guid DeviceId, string AgentWorkerId,
    FuelType FuelType, long VolumeMl, long UnitPricePiastres, long AmountPiastres, bool Billable, DateTimeOffset EvaluatedAt,
    DateTimeOffset DeviceAt, DateTimeOffset ReceivedAt)
{
    public static FuelingResponse Of(Fueling f) => new(f.Id, f.Reference, f.Status, f.DeclineReason, f.Conflicts, f.Origin, f.CompanyId,
        f.CompanyNameAr, f.CompanyNameEn, f.VehicleId, f.Plate, f.OperatorId, f.StationId, f.StationNameAr, f.StationNameEn, f.DeviceId,
        f.AgentWorkerId, f.FuelType, f.VolumeMl, f.UnitPricePiastres, f.AmountPiastres, f.Billable, f.EvaluatedAt, f.DeviceAt, f.ReceivedAt);

    public static Page<FuelingResponse> PageOf(Page<Fueling> page) => new(page.Items.Select(Of).ToList(), page.NextCursor);
}

/// <summary>The printable receipt behind a signed link: no ids, nothing the bearer could use elsewhere.</summary>
public sealed record ReceiptResponse(string Reference, FuelingStatus Status, FuelingRules.DeclineReason? DeclineReason, string Plate,
    string CompanyNameAr, string CompanyNameEn, string StationNameAr, string StationNameEn, string AgentWorkerId, FuelType FuelType,
    long VolumeMl, long UnitPricePiastres, long AmountPiastres, bool Billable, DateTimeOffset EvaluatedAt, string ReceiptLocale)
{
    /// <summary>The Worker ID is half of the agent's sign-in, so a public receipt shows only its last three characters.</summary>
    public static ReceiptResponse Of(Fueling f)
    {
        var worker = f.AgentWorkerId;
        var masked = worker.Length <= 3 ? worker : new string('•', worker.Length - 3) + worker[^3..];
        return new(f.Reference, f.Status, f.DeclineReason, f.Plate, f.CompanyNameAr, f.CompanyNameEn, f.StationNameAr, f.StationNameEn, masked,
            f.FuelType, f.VolumeMl, f.UnitPricePiastres, f.AmountPiastres, f.Billable, f.EvaluatedAt, f.ReceiptLocale);
    }
}

public sealed record AlertResponse(Guid Id, AlertType Type, AlertSeverity Severity, AlertStatus Status, Guid? CompanyId, Guid? OperatorId,
    Guid? StationId, Guid? DeviceId, Guid? VehicleId, Guid? FuelingId, IReadOnlyDictionary<string, JsonElement> Details,
    DateTimeOffset CreatedAt, Guid? AcknowledgedBy, DateTimeOffset? AcknowledgedAt)
{
    public static AlertResponse Of(Alert a) => new(a.Id, a.Type, a.Severity, a.Status, a.CompanyId, a.OperatorId, a.StationId, a.DeviceId,
        a.VehicleId, a.FuelingId, a.Details, a.CreatedAt, a.AcknowledgedBy, a.AcknowledgedAt);

    public static Page<AlertResponse> PageOf(Page<Alert> page) => new(page.Items.Select(Of).ToList(), page.NextCursor);
}
