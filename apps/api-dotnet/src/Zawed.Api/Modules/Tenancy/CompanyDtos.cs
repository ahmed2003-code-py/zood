using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Tenancy;

// The request and response records of the companies and company portal endpoints, named and shaped as the Java
// records, so the OpenAPI document and the JSON are the same. A property the Java document marks required is
// non-nullable here, or carries [NotNull] / [NotBlank].

/// <summary>A company's identity and contacts, as Ops types them. Contact numbers are cleaned and judged by the service.</summary>
public sealed record CompanyBody(
    [NotBlank, Size(max: 200)] string LegalName,
    [NotBlank, Size(max: 200)] string NameAr,
    [NotBlank, Size(max: 200)] string NameEn,
    [Size(max: 50)] string? TaxId,
    [Size(max: 50)] string? CommercialRegistration,
    [Size(max: 200)] string? ContactName,
    [Size(max: 30)] string? ContactPhone,
    [Email, Size(max: 254)] string? ContactEmail);

public sealed record CreateCompanyRequest(
    [NotNull] CompanyBody Company,
    [Min(120), Max(240)] int? CooldownMinutes,
    [NotNull, PositiveOrZero] long? LowBalanceThresholdPiastres);

public sealed record CompanyPolicyRequest(
    [NotNull, Min(120), Max(240)] int? CooldownMinutes,
    [NotNull, PositiveOrZero] long? LowBalanceThresholdPiastres,
    [NotBlank, Size(max: 500)] string Reason);

public sealed record CompanyStatusRequest(
    [NotNull] CompanyStatus? Status,
    [NotBlank, Size(max: 500)] string Reason);

public sealed record CreateDepartmentRequest(
    [NotBlank, Size(max: 120)] string Name,
    [PositiveOrZero] long? MonthlyBudgetPiastres);

public sealed record CompanyResponse(
    Guid Id,
    string LegalName,
    string NameAr,
    string NameEn,
    string? TaxId,
    string? CommercialRegistration,
    string? ContactName,
    string? ContactPhone,
    string? ContactEmail,
    CompanyStatus Status,
    int CooldownMinutes,
    long LowBalanceThresholdPiastres,
    DateTimeOffset CreatedAt);

public sealed record DepartmentResponse(Guid Id, string Name, long? MonthlyBudgetPiastres, bool Active);

/// <summary>What a company sees about itself: no tax id, no contacts, no other company's data.</summary>
public sealed record CompanyProfile(
    Guid Id,
    string LegalName,
    string NameAr,
    string NameEn,
    CompanyStatus Status,
    int CooldownMinutes,
    long LowBalanceThresholdPiastres);
