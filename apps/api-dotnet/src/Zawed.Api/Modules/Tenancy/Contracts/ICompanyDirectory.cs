namespace Zawed.Api.Modules.Tenancy.Contracts;

/// <summary>
/// A company as other modules need it: identity, status, the cooldown that applies to its tags and the balance
/// under which its wallet counts as low (PRD §4.7).
/// </summary>
public sealed record CompanyRef(Guid Id, string NameAr, string NameEn, bool Active, int CooldownMinutes, long LowBalanceThresholdPiastres);

/// <summary>
/// What other modules may ask about companies (Java <c>tenancy.CompanyDirectory</c>). Reads run in the caller's
/// transaction and scope, so a company outside it is simply not found.
/// </summary>
public interface ICompanyDirectory
{
    Task<CompanyRef?> FindAsync(Guid companyId, CancellationToken ct = default);

    /// <summary>The companies with these ids that the caller's scope can see, in no particular order.</summary>
    Task<IReadOnlyList<CompanyRef>> FindAllAsync(IReadOnlyCollection<Guid> companyIds, CancellationToken ct = default);

    /// <summary>True if the department exists, is active and belongs to the company.</summary>
    Task<bool> HasActiveDepartmentAsync(Guid companyId, Guid departmentId, CancellationToken ct = default);
}
