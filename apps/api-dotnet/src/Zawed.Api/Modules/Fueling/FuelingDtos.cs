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
