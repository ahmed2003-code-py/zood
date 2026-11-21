# Multi-Tenancy — Global Query Filter بحيث أي كويري تتفلتر بالـ tenant أوتوماتيك

## الوضع في المشاريع (بصراحة)

| المشروع | الـ Tenant | الطريقة |
|---|---|---|
| **Shifa** | `ClinicId` (عيادات) + `PharmacyId` (صيدليات/فروع) | ✅ **Global Query Filter** على interface + guard في `SaveChangesAsync` ← **ده النموذج المعتمد** |
| NED-Order | `RestaurantId` (+ `BranchId` للـ BranchManager) | يدوي: الـ controller بيقرأ الـ claim ويبعته لكل method في السيرفس (`GetOrderByIdAsync(RestaurantId, orderId)`). مفيش `HasQueryFilter` في `AppDbContext` |
| Nedx/CRM | `CompanyId` | يدوي: `GetCompanyId()` من الـ claim في كل controller |

الطريقة اليدوية بتشتغل، بس لو نسيت `Where(x => x.RestaurantId == rid)` في كويري واحدة، الداتا هتتسرب لمطعم تاني. علشان كده **في أي مشروع جديد multi-tenant: طبّق نموذج Shifa تحت**. ولو هتنقل NED-Order له: اعمل `IRestaurantScoped { int RestaurantId }` على الـ entities واتبع نفس الخطوات.

## 1) الـ interface على كل entity تابعة لـ tenant

```csharp
public interface ITenantEntity { Guid ClinicId { get; set; } }      // أو int RestaurantId / int CompanyId
public interface IPharmacyScoped { Guid PharmacyId { get; set; } }   // ينفع أكتر من نوع tenant في نفس السيستم

public class Appointment : BaseEntity, ITenantEntity { public Guid ClinicId { get; set; } /* ... */ }
```

## 2) ICurrentUser: مصدر واحد لهوية المستخدم والـ tenant

```csharp
public interface ICurrentUser
{
    Guid? UserId { get; }
    string? Role { get; }
    IReadOnlyCollection<Guid> ClinicIds { get; }   // من الـ JWT claims
    Guid? PharmacyId { get; }                      // الفرع الحالي: هيدر X-Branch لو مسموح له، وإلا أول فرع
    IReadOnlyCollection<Guid> PharmacyIds { get; }
    bool IsAdmin { get; }
}

public class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? User => accessor.HttpContext?.User;
    public Guid? UserId => Guid.TryParse(User?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    public string? Role => User?.FindFirstValue(ClaimTypes.Role);
    public IReadOnlyCollection<Guid> ClinicIds => User?.FindAll("clinic").Select(c => Guid.Parse(c.Value)).ToArray() ?? [];
    public IReadOnlyCollection<Guid> PharmacyIds => User?.FindAll("pharmacy").Select(c => Guid.Parse(c.Value)).ToArray() ?? [];
    public Guid? PharmacyId
    {
        get
        {
            var all = PharmacyIds; if (all.Count == 0) return null;
            var header = accessor.HttpContext?.Request.Headers["X-Branch"].ToString();
            return Guid.TryParse(header, out var b) && all.Contains(b) ? b : all.First();   // مايقدرش يختار فرع مش بتاعه
        }
    }
    public bool IsAdmin => Role == Roles.Admin;
}
// Program.cs
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
```

الـ tenant ids بتتحط في الـ JWT وقت الـ login (`claims.Add(new Claim("clinic", id.ToString()))` لكل عيادة).

## 3) الـ filter في الـ DbContext (أوتوماتيك على كل entity بتطبق الـ interface)

```csharp
public class ShifaDbContext(DbContextOptions<ShifaDbContext> options, ICurrentUser currentUser) : IdentityDbContext<...>(options)
{
    // properties على الـ context نفسه ← EF بيعملها parameter وبيقيّمها مع كل كويري، مش مرة واحدة وقت بناء الموديل
    public bool TenantScoped => currentUser.IsClinicStaff;                 // الأدمن والمريض مش متقيدين بعيادة
    public Guid[] TenantClinicIds => currentUser.ClinicIds.ToArray();
    public bool PharmacyScoped => currentUser.Role == Roles.Pharmacist;
    public Guid PharmacyTenantId => currentUser.PharmacyId ?? Guid.Empty;

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        // ... باقي الـ configuration

        foreach (var type in b.Model.GetEntityTypes().Where(t => typeof(ITenantEntity).IsAssignableFrom(t.ClrType)))
            GetType().GetMethod(nameof(ApplyTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(type.ClrType).Invoke(this, [b]);

        foreach (var type in b.Model.GetEntityTypes().Where(t => typeof(IPharmacyScoped).IsAssignableFrom(t.ClrType)))
            GetType().GetMethod(nameof(ApplyPharmacyFilter), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(type.ClrType).Invoke(this, [b]);
    }

    private void ApplyTenantFilter<T>(ModelBuilder b) where T : class, ITenantEntity
    {
        Expression<Func<T, bool>> filter = e => !TenantScoped || TenantClinicIds.Contains(e.ClinicId);
        b.Entity<T>().HasQueryFilter(filter);
    }

    private void ApplyPharmacyFilter<T>(ModelBuilder b) where T : class, IPharmacyScoped
    {
        Expression<Func<T, bool>> filter = e => !PharmacyScoped || e.PharmacyId == PharmacyTenantId;
        b.Entity<T>().HasQueryFilter(filter);
    }
}
```

دلوقتي `db.Appointments.Where(a => a.Date == today)` بتطلع في الـ SQL `... AND ClinicId IN (@ids)` من غير ما السيرفس تكتب حاجة.

**نسخة tenant واحد (مطعم/شركة):**
```csharp
public int? CurrentRestaurantId => currentUser.RestaurantId;      // null = super admin أو anonymous
Expression<Func<T, bool>> filter = e => CurrentRestaurantId == null || e.RestaurantId == CurrentRestaurantId;
```
> ⚠️ لو فيه endpoints عامة (storefront أو منيو للزباين من غير login)، الـ `CurrentRestaurantId` هيبقى null وهيشوف كل حاجة. فلازم الـ public queries تفلتر بالـ slug/restaurantId صراحة، أو `TenantScoped` يبقى false بس للأدمن والـ public يتعامل explicit.

## 4) Guard على الكتابة: يمنع الكتابة في tenant تاني حتى لو السيرفس نسيت تتشيك

```csharp
public override Task<int> SaveChangesAsync(CancellationToken ct = default)
{
    if (TenantScoped)
    {
        var ids = TenantClinicIds;
        var leak = ChangeTracker.Entries<ITenantEntity>()
            .FirstOrDefault(e => e.State is EntityState.Added or EntityState.Modified && !ids.Contains(e.Entity.ClinicId));
        if (leak is not null) throw new UnauthorizedAccessException("Cross-tenant write blocked");   // الـ middleware يرجعها 403
    }
    if (PharmacyScoped)
    {
        var ph = PharmacyTenantId;
        var leak = ChangeTracker.Entries<IPharmacyScoped>()
            .FirstOrDefault(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                                 && e.Entity.PharmacyId != ph && !extraBranches.Contains(e.Entity.PharmacyId));
        if (leak is not null) throw new UnauthorizedAccessException("Cross-pharmacy write blocked");
    }
    return base.SaveChangesAsync(ct);
}

// استثناء مقصود ومحدود: تحويل مخزون بين فرعين بتوع نفس المستخدم
private readonly HashSet<Guid> extraBranches = [];
public void AllowBranches(params Guid[] ids) { foreach (var id in ids) if (currentUser.PharmacyIds.Contains(id)) extraBranches.Add(id); }
// في السيرفس: db.AllowBranches(req.ToPharmacyId);
```

## 5) قواعد التعامل

- **`IgnoreQueryFilters()`** بس في حالات مقصودة: بيانات عامة (العيادة اللي المريض بيحجز فيها)، أو jobs، أو الأدمن، أو lookups للإشعارات. ولازم بعدها **filter صريح** بالـ id (`.IgnoreQueryFilters().Where(c => c.Id == clinicId)`).
- **الـ soft delete** ينفع يتجمع في نفس الـ filter: `e => !e.IsDeleted && (...)`، لأن EF بيسمح بـ `HasQueryFilter` واحد بس لكل entity في الإصدارات قبل EF 10، فالشروط لازم تتدمج في expression واحدة.
- **Indexes:** أي composite index على جدول tenant **يبدأ بالـ tenant id** (`{ RestaurantId, Status }`، `{ ClinicId, Date }`).
- **Unique per tenant:** `HasIndex(x => new { x.PharmacyId, x.Number }).IsUnique()` (رقم فاتورة فريد لكل صيدلية).
- **سحب الصلاحية فوراً:** الـ token فيه الـ tenants لحد ما يخلص. Shifa بيعمل middleware (`UseClinicMembershipCheck` في `Modules/Shared/Live.cs`) بيتشيك على العضوية الحالية بـ lookup واحد عليه index، ولو اتشال يرجع 401.
- **Background jobs** مفيهاش HttpContext، فـ `ICurrentUser` بيرجع null/empty، والفلاتر بتتقفل لوحدها (`TenantScoped = false`). علشان كده الـ job لازم تفلتر صراحة.
