using Microsoft.EntityFrameworkCore;
using Zawed.Api.Modules.Tenancy.Contracts;
using Zawed.Api.Platform.Persistence;

namespace Zawed.Api.Modules.Tenancy;

/// <summary><see cref="ICompanyDirectory"/> on the caller's transaction: row-level security and the EF filter decide what it finds.</summary>
public sealed class CompanyDirectory(IDbSession session) : ICompanyDirectory
{
    private ZawedDbContext Db => session.Current.Db();

    public Task<CompanyRef?> FindAsync(Guid companyId, CancellationToken ct = default) =>
        Db.Set<Company>().Where(c => c.Id == companyId)
            .Select(c => new CompanyRef(c.Id, c.NameAr, c.NameEn, c.Status == CompanyStatus.ACTIVE, c.CooldownMinutes, c.LowBalanceThresholdPiastres))
            .SingleOrDefaultAsync(ct);

    public async Task<IReadOnlyList<CompanyRef>> FindAllAsync(IReadOnlyCollection<Guid> companyIds, CancellationToken ct = default)
    {
        if (companyIds.Count == 0)
        {
            return [];
        }
        return await Db.Set<Company>().Where(c => companyIds.Contains(c.Id))
            .Select(c => new CompanyRef(c.Id, c.NameAr, c.NameEn, c.Status == CompanyStatus.ACTIVE, c.CooldownMinutes, c.LowBalanceThresholdPiastres))
            .ToListAsync(ct);
    }

    public Task<bool> HasActiveDepartmentAsync(Guid companyId, Guid departmentId, CancellationToken ct = default) =>
        Db.Set<Department>().AnyAsync(d => d.Id == departmentId && d.CompanyId == companyId && d.Status == DepartmentStatus.ACTIVE, ct);
}
