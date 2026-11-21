using Zawed.Api.Platform.Text;
using Zawed.Api.Platform.Web;
using Zawed.Domain;

namespace Zawed.Api.Modules.Network;

// Request and response bodies of the network endpoints (Java NetworkApi, PosController, Shifts), named and shaped as
// the Java records so the OpenAPI document and the JSON are the same.

// ----------------------------------------------------------------------------------------------- operators

/// <summary>An operator's identity and contacts, as Ops types them. Contact numbers are cleaned and judged by the service.</summary>
public sealed record OperatorBody(
    [NotBlank, Size(max: 200)] string LegalName,
    [NotBlank, Size(max: 200)] string NameAr,
    [NotBlank, Size(max: 200)] string NameEn,
    [Size(max: 50)] string? TaxId,
    [Size(max: 50)] string? CommercialRegistration,
    [Size(max: 200)] string? ContactName,
    [Size(max: 30)] string? ContactPhone,
    [Email, Size(max: 254)] string? ContactEmail);

public sealed record OperatorStatusRequest(
    [NotNull] OperatorStatus? Status,
    [NotBlank, Size(max: 500)] string Reason);

public sealed record OperatorResponse(
    Guid Id,
    string LegalName,
    string NameAr,
    string NameEn,
    string? TaxId,
    string? CommercialRegistration,
    string? ContactName,
    string? ContactPhone,
    string? ContactEmail,
    OperatorStatus Status,
    DateTimeOffset CreatedAt);

// ------------------------------------------------------------------------------------------------ stations

public sealed record StationBody(
    [NotBlank, Size(max: 200)] string NameAr,
    [NotBlank, Size(max: 200)] string NameEn,
    [Size(max: 500)] string? Address,
    [DecimalMin(-90), DecimalMax(90), Digits(2, 6)] Coordinate? Latitude,
    [DecimalMin(-180), DecimalMax(180), Digits(3, 6)] Coordinate? Longitude,
    [Size(max: 64)] string? TimeZone,
    [NotNull] Connectivity? Connectivity,
    [NotEmpty] HashSet<FuelType>? FuelTypes);

public sealed record CreateStationRequest(
    [NotNull] Guid? OperatorId,
    [NotNull] StationBody? Station);

public sealed record StationStatusRequest(
    [NotNull] StationStatus? Status,
    [NotBlank, Size(max: 500)] string Reason);

public sealed record StationResponse(
    Guid Id,
    Guid OperatorId,
    string NameAr,
    string NameEn,
    string? Address,
    Coordinate? Latitude,
    Coordinate? Longitude,
    string TimeZone,
    Connectivity Connectivity,
    IReadOnlyList<FuelType> FuelTypes,
    StationStatus Status,
    DateTimeOffset CreatedAt);

// ------------------------------------------------------------------------------------------------- devices

public sealed record RegisterDeviceRequest(
    [NotNull] Guid? StationId,
    [Size(max: 100)] string? Label);

public sealed record RevokeDeviceRequest([NotBlank, Size(max: 500)] string Reason);

public sealed record DeviceResponse(
    Guid Id,
    Guid OperatorId,
    Guid StationId,
    string Code,
    string? Label,
    DeviceStatus Status,
    DateTimeOffset? ActivationExpiresAt,
    bool ActivationExpired,
    DateTimeOffset? BoundAt,
    DateTimeOffset? LastSyncAt,
    DateTimeOffset? RevokedAt,
    string? RevokedReason,
    DateTimeOffset CreatedAt);

/// <summary>Returned once, when the code is generated. It cannot be read back later.</summary>
public sealed record DeviceActivationResponse(DeviceResponse Device, string ActivationCode);

// -------------------------------------------------------------------------------------------------- agents

/// <summary>
/// A new agent. The phone is cleaned before it is validated, as the Java record's compact constructor did:
/// <c>+20 112 345 6789</c> is stored as <c>01123456789</c>, and blank is no phone.
/// </summary>
public sealed record CreateAgentRequest(
    [NotNull] Guid? StationId,
    [NotBlank, Size(max: 200)] string FullName,
    string? Phone)
{
    [Pattern(EgyptianContact.Mobile)]
    public string? Phone { get; init; } = EgyptianContact.OrNull(EgyptianContact.PhoneOf(Phone));
}

public sealed record AgentStatusRequest(
    [NotNull] AgentStatus? Status,
    [NotBlank, Size(max: 500)] string Reason);

public sealed record AgentResponse(
    Guid Id,
    Guid OperatorId,
    Guid StationId,
    string WorkerId,
    string FullName,
    string? Phone,
    AgentStatus Status,
    bool MustChangePin,
    DateTimeOffset? LockedUntil,
    bool Locked,
    DateTimeOffset CreatedAt);

/// <summary>Returned once, when the PIN is generated. It cannot be read back later.</summary>
public sealed record AgentPinResponse(AgentResponse Agent, string Pin);

// -------------------------------------------------------------------------------------------------- shifts

/// <summary>An agent's session on a device, from sign-in to sign-out, revocation or expiry (plan §7 B6).</summary>
public sealed record Shift(
    Guid Id,
    Guid AgentId,
    string WorkerId,
    string AgentName,
    Guid DeviceId,
    string DeviceCode,
    Guid StationId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? EndReason);

// ----------------------------------------------------------------------------------------------------- POS

public sealed record ActivateRequest(
    [NotBlank, Size(max: 40)] string DeviceCode,
    [NotBlank, Size(max: 40)] string ActivationCode,
    [NotBlank, Size(max: 512)] string PublicKey);

public sealed record ActivateResponse(Guid DeviceId, string DeviceCode, Guid StationId);

public sealed record DeviceTokenRequest(
    [NotNull] Guid? DeviceId,
    [NotNull] long? Timestamp,
    [NotBlank, Size(min: 16, max: 128)] string Nonce,
    [NotBlank, Size(max: 256)] string Signature);

public sealed record DeviceTokenResponse(string Token, DateTimeOffset ExpiresAt);

public sealed record AgentLoginRequest(
    [NotBlank, Size(max: 20)] string WorkerId,
    [NotBlank, Size(max: 20)] string Pin);

public sealed record PosAgentResponse(Guid Id, string WorkerId, string FullName, bool MustChangePin);

public sealed record AgentLoginResponse(string SessionToken, DateTimeOffset ExpiresAt, PosAgentResponse Agent);

public sealed record ChangePinRequest(
    [NotBlank, Size(max: 20)] string CurrentPin,
    [NotBlank, Size(max: 20)] string NewPin);
