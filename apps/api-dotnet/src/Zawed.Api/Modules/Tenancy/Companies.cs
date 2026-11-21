using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Zawed.Api.Modules.Tenancy.Contracts;
using Zawed.Api.Platform.Events;
using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Text;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Tenancy;

/// <summary>
/// Companies and their departments (PRD §6.3 Onboard Company; Java <c>tenancy.Companies</c>). Ops onboards and
/// edits; the company itself only reads (approved decision: company data is view-only, changes go through
/// requests). Every read and write runs on the request's transaction, under row-level security, through the EF
/// context (the Unit of Work); every change is audited in the same transaction.
/// </summary>
public sealed class Companies(IDbSession session, TimeProvider clock, IdempotencyKeys keys, IDomainEvents events, IAuditLog audit)
{
    /// <summary>Default cooling-off: the middle of PRD's 2–4 hour range.</summary>
    public const int DefaultCooldownMinutes = 180;

    private ZawedDbContext Db => session.Current.Db();

    private static readonly Expression<Func<Company, CompanyResponse>> ToResponse = c => new CompanyResponse(c.Id, c.LegalName,
        c.NameAr, c.NameEn, c.TaxId, c.CommercialRegistration, c.ContactName, c.ContactPhone, c.ContactEmail, c.Status,
        c.CooldownMinutes, c.LowBalanceThresholdPiastres, c.CreatedAt);

    private static readonly Expression<Func<Department, DepartmentResponse>> ToDepartment = d =>
        new DepartmentResponse(d.Id, d.Name, d.MonthlyBudgetPiastres, d.Status == DepartmentStatus.ACTIVE);

    // ------------------------------------------------------------------------------------------------- queries

    public async Task<Result<Page<CompanyResponse>>> ListAsync(CompanyStatus? status, string? query, string? cursor, int? limit,
        CancellationToken ct)
    {
        var size = Page<CompanyResponse>.Limit(limit);
        var after = Cursors.Parse(cursor);
        var companies = Db.Set<Company>().AsQueryable();
        if (status is { } wanted)
        {
            companies = companies.Where(c => c.Status == wanted);
        }
        if (Like(query) is { } pattern)
        {
            // Any spelling of a tax id finds it: the same key as the unique index (V23).
            var raw = Sql.Clean(query);
            companies = companies.Where(c => EF.Functions.Like(c.LegalName.ToLower(), pattern, @"\")
                || EF.Functions.Like(c.NameAr.ToLower(), pattern, @"\") || EF.Functions.Like(c.NameEn.ToLower(), pattern, @"\")
                || Company.TaxIdKey(c.TaxId) == Company.TaxIdKey(raw));
        }
        if (after is { } last)
        {
            companies = companies.Where(c => c.Id < last);
        }
        var rows = await companies.OrderByDescending(c => c.Id).Take(size + 1).Select(ToResponse).ToListAsync(ct);
        return Page<CompanyResponse>.Of(rows, size, c => c.Id);
    }

    public async Task<Result<CompanyResponse>> GetAsync(Guid id, CancellationToken ct) =>
        await Db.Set<Company>().Where(c => c.Id == id).Select(ToResponse).SingleOrDefaultAsync(ct) is { } company
            ? company
            : Result<CompanyResponse>.NotFound("Company");

    public async Task<Result<CompanyProfile>> ProfileAsync(Guid id, CancellationToken ct) =>
        await Db.Set<Company>().Where(c => c.Id == id)
            .Select(c => new CompanyProfile(c.Id, c.LegalName, c.NameAr, c.NameEn, c.Status, c.CooldownMinutes, c.LowBalanceThresholdPiastres))
            .SingleOrDefaultAsync(ct) is { } profile
            ? profile
            : Result<CompanyProfile>.NotFound("Company");

    public async Task<Result<Page<DepartmentResponse>>> DepartmentsAsync(Guid companyId, string? cursor, int? limit, CancellationToken ct)
    {
        if (!await ExistsAsync(companyId, ct))
        {
            return Result<Page<DepartmentResponse>>.NotFound("Company"); // also a company outside the caller's scope
        }
        var size = Page<DepartmentResponse>.Limit(limit);
        var after = Cursors.Parse(cursor);
        var departments = Db.Set<Department>().Where(d => d.CompanyId == companyId);
        if (after is { } last)
        {
            departments = departments.Where(d => d.Id < last);
        }
        var rows = await departments.OrderByDescending(d => d.Id).Take(size + 1).Select(ToDepartment).ToListAsync(ct);
        return Page<DepartmentResponse>.Of(rows, size, d => d.Id);
    }

    // ------------------------------------------------------------------------------------------------- changes

    /// <summary>
    /// Onboards a company and, in the same transaction, whatever <see cref="CompanyCreated"/>'s handlers set up (the
    /// ledger opens its wallet). With an Idempotency-Key (ADR 0022) a repeat of the same request answers the company
    /// it created (200); the tax id, when given, stays a second line of defence. The commercial registration is not
    /// unique: registries are per governorate, and the owner has not confirmed a uniqueness rule.
    /// </summary>
    public async Task<Result<CompanyResponse>> CreateAsync(ZawedPrincipal actor, string? idempotencyKey, CreateCompanyRequest request,
        CancellationToken ct)
    {
        var check = new EgyptianContact.Check("company.");
        var draft = Draft.Of(request.Company, null, check);
        if (check.Issues.Count > 0)
        {
            return Result<CompanyResponse>.Invalid([.. check.Issues]);
        }
        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        if (await keys.ClaimAsync(actor.UserId, "company.create", idempotencyKey, request, id, ct) is { } earlier)
        {
            return await GetAsync(earlier, ct);
        }
        var cooldown = request.CooldownMinutes ?? DefaultCooldownMinutes;
        var threshold = request.LowBalanceThresholdPiastres!.Value;
        var company = new Company
        {
            Id = id, Status = CompanyStatus.ACTIVE, CooldownMinutes = cooldown, LowBalanceThresholdPiastres = threshold,
            CreatedAt = now, CreatedBy = actor.UserId, UpdatedAt = now, UpdatedBy = actor.UserId,
        };
        draft.ApplyTo(company);
        Db.Add(company);
        if (await SaveAsync(ct) is { } refused)
        {
            return Result<CompanyResponse>.From(refused);
        }
        await audit.RecordAsync(session.Current, ActorOf(actor), "COMPANY_CREATED", "COMPANY", id, Scope.Company(id),
            new Dictionary<string, object?> { ["cooldownMinutes"] = cooldown, ["lowBalanceThresholdPiastres"] = threshold }, ct);
        // Synchronous: handlers run here, in this transaction. A company without its wallet never commits.
        await events.PublishAsync(new CompanyCreated(id), ct);
        return Result<CompanyResponse>.Created((await GetAsync(id, ct)).Data!);
    }

    public async Task<Result<CompanyResponse>> UpdateAsync(ZawedPrincipal actor, Guid id, CompanyBody body, CancellationToken ct)
    {
        if (await LockAsync(id, ct) is not { } company)
        {
            return Result<CompanyResponse>.NotFound("Company");
        }
        var legalNameBefore = company.LegalName;
        var check = new EgyptianContact.Check("");
        var draft = Draft.Of(body, company, check);
        if (check.Issues.Count > 0)
        {
            return Result<CompanyResponse>.Invalid([.. check.Issues]);
        }
        draft.ApplyTo(company);
        Touch(company, actor);
        if (await SaveAsync(ct) is { } refused)
        {
            return Result<CompanyResponse>.From(refused);
        }
        await audit.RecordAsync(session.Current, ActorOf(actor), "COMPANY_UPDATED", "COMPANY", id, Scope.Company(id),
            new Dictionary<string, object?> { ["legalNameBefore"] = legalNameBefore }, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Cooldown and low-balance threshold: financial control settings, Super Admin only.</summary>
    public async Task<Result<CompanyResponse>> UpdatePolicyAsync(ZawedPrincipal actor, Guid id, CompanyPolicyRequest request, CancellationToken ct)
    {
        if (await LockAsync(id, ct) is not { } company)
        {
            return Result<CompanyResponse>.NotFound("Company");
        }
        var cooldown = request.CooldownMinutes!.Value;
        var threshold = request.LowBalanceThresholdPiastres!.Value;
        var change = new Dictionary<string, object?>
        {
            ["cooldownMinutes"] = new Dictionary<string, object> { ["from"] = company.CooldownMinutes, ["to"] = cooldown },
            ["lowBalanceThresholdPiastres"] = new Dictionary<string, object> { ["from"] = company.LowBalanceThresholdPiastres, ["to"] = threshold },
            ["reason"] = JavaTrim(request.Reason),
        };
        company.CooldownMinutes = cooldown;
        company.LowBalanceThresholdPiastres = threshold;
        Touch(company, actor);
        await Db.SaveChangesAsync(ct);
        await audit.RecordAsync(session.Current, ActorOf(actor), "COMPANY_POLICY_CHANGED", "COMPANY", id, Scope.Company(id), change, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Freeze or unfreeze (PRD §6.3 Freeze Account). A frozen company's vehicles cannot fuel.</summary>
    public async Task<Result<CompanyResponse>> SetStatusAsync(ZawedPrincipal actor, Guid id, CompanyStatusRequest request, CancellationToken ct)
    {
        if (await LockAsync(id, ct) is not { } company)
        {
            return Result<CompanyResponse>.NotFound("Company");
        }
        var status = request.Status!.Value;
        if (company.Status != status)
        {
            company.Status = status;
            Touch(company, actor);
            await Db.SaveChangesAsync(ct);
            await audit.RecordAsync(session.Current, ActorOf(actor), status == CompanyStatus.FROZEN ? "COMPANY_FROZEN" : "COMPANY_UNFROZEN", "COMPANY", id,
                Scope.Company(id), new Dictionary<string, object?> { ["reason"] = JavaTrim(request.Reason) }, ct);
        }
        return await GetAsync(id, ct);
    }

    public async Task<Result<DepartmentResponse>> AddDepartmentAsync(ZawedPrincipal actor, Guid companyId, CreateDepartmentRequest request,
        CancellationToken ct)
    {
        if (!await ExistsAsync(companyId, ct))
        {
            return Result<DepartmentResponse>.NotFound("Company");
        }
        var now = clock.GetUtcNow();
        var name = Sql.Clean(request.Name)!;
        var department = new Department
        {
            Id = Guid.CreateVersion7(now), CompanyId = companyId, Name = name, MonthlyBudgetPiastres = request.MonthlyBudgetPiastres,
            Status = DepartmentStatus.ACTIVE, CreatedAt = now, CreatedBy = actor.UserId, UpdatedAt = now, UpdatedBy = actor.UserId,
        };
        Db.Add(department);
        if (await SaveAsync(ct) is { } refused)
        {
            return Result<DepartmentResponse>.From(refused);
        }
        await audit.RecordAsync(session.Current, ActorOf(actor), "DEPARTMENT_CREATED", "DEPARTMENT", department.Id, Scope.Company(companyId),
            new Dictionary<string, object?> { ["name"] = name }, ct);
        return Result<DepartmentResponse>.Created(
            await Db.Set<Department>().Where(d => d.Id == department.Id).Select(ToDepartment).SingleAsync(ct));
    }

    // ------------------------------------------------------------------------------------------------- helpers

    private Task<bool> ExistsAsync(Guid id, CancellationToken ct) => Db.Set<Company>().AnyAsync(c => c.Id == id, ct);

    /// <summary>
    /// Reads the company and holds its row until commit, so two changes never both read the same "before"
    /// (<c>SELECT ... FOR UPDATE</c>; EF has no locking operator, so the statement is written out). Tracked, so
    /// the change is saved by <see cref="ZawedDbContext.SaveChangesAsync(CancellationToken)"/>.
    /// </summary>
    private Task<Company?> LockAsync(Guid id, CancellationToken ct) =>
        Db.Set<Company>().FromSql($"select * from companies where id = {id} for update").AsTracking().SingleOrDefaultAsync(ct);

    /// <summary>Java's <c>String.trim()</c>: every character up to U+0020 off both ends, as the Java controller trimmed a reason.</summary>
    private static string JavaTrim(string value) => value.Trim(JavaTrimmed);

    private static readonly char[] JavaTrimmed = [.. Enumerable.Range(0, 33).Select(c => (char)c)];

    private void Touch(Company company, ZawedPrincipal actor)
    {
        company.UpdatedAt = clock.GetUtcNow();
        company.UpdatedBy = actor.UserId;
    }

    /// <summary>
    /// Saves the pending change. A unique index the change breaks is the refusal the Java code gave for it; the
    /// failed statement has aborted the transaction, which the refusal's status rolls back.
    /// </summary>
    private async Task<Result?> SaveAsync(CancellationToken ct)
    {
        try
        {
            await Db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex, CompanyConfiguration.TaxIdIndex))
        {
            return Result.Fail(409, "TAX_ID_IN_USE", "Another company has this tax id", "taxId");
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex, DepartmentConfiguration.NameIndex))
        {
            return Result.Fail(409, "DEPARTMENT_EXISTS", "A department with this name exists", "name");
        }
    }

    /// <summary>A search as a LIKE pattern on lower-cased text: <c>%</c>, <c>_</c> and <c>\</c> are matched literally.</summary>
    internal static string? Like(string? query) => Sql.Clean(query) is { } cleaned
        ? "%" + cleaned.ToLowerInvariant().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%"
        : null;

    /// <summary>The body with its contact numbers cleaned and judged (QA round 1), against the stored company on an update.</summary>
    private sealed record Draft(string? LegalName, string? NameAr, string? NameEn, string? TaxId, string? CommercialRegistration,
        string? ContactName, string? ContactPhone, string? ContactEmail)
    {
        public static Draft Of(CompanyBody body, Company? before, EgyptianContact.Check check) => new(
            Sql.Clean(body.LegalName), Sql.Clean(body.NameAr), Sql.Clean(body.NameEn),
            check.TaxId("taxId", body.TaxId, before?.TaxId),
            check.CommercialRegistration("commercialRegistration", body.CommercialRegistration, before?.CommercialRegistration),
            Sql.Clean(body.ContactName),
            check.Phone("contactPhone", body.ContactPhone, before?.ContactPhone),
            Sql.Clean(body.ContactEmail)?.ToLowerInvariant());

        public void ApplyTo(Company company)
        {
            company.LegalName = LegalName!;
            company.NameAr = NameAr!;
            company.NameEn = NameEn!;
            company.TaxId = Sql.Clean(TaxId);
            company.CommercialRegistration = Sql.Clean(CommercialRegistration);
            company.ContactName = ContactName;
            company.ContactPhone = Sql.Clean(ContactPhone);
            company.ContactEmail = ContactEmail;
        }
    }

    private static AuditActor ActorOf(ZawedPrincipal actor) => new(actor.UserId, actor.Role);
}
