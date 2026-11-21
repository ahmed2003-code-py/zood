# Data Access — Generic Repository, Unit of Work, Query Optimization, Indexing, Pagination

## 1) Generic Repository (نسخة KMG — المعتمدة)

القاعدة الأساسية: **الريبو مابيعملش `SaveChanges`**. الحفظ مسؤولية الـ UoW (`CompleteAsync`) أو الـ `TransactionActionFilter`.
> NED-Order عنده `AddAsync`/`DeleteAsync`/`Update` بيعملوا `SaveChanges` جوه الريبو. ده بيكسر فكرة الـ UoW (كل Add بيروح الداتابيز لوحده)، ومش هننقله لمشروع جديد.

```csharp
// Core/Interfaces/IBaseRepository.cs
public interface IBaseRepository<T> where T : class
{
    Task<IEnumerable<T>> GetAllAsync();
    Task<T?> GetByIdAsync(int id);
    Task<T> AddAsync(T entity);
    Task AddRangeAsync(IEnumerable<T> entities);
    T Update(T entity);
    void Delete(T entity);
    void DeleteRange(IEnumerable<T> entities);

    Task<T?> FindTWithIncludes(int id, string keyName, params Expression<Func<T, object>>[] includes);
    Task<T?> FindTWithQueryIncludes(int id, string keyName, Func<IQueryable<T>, IQueryable<T>>? includes = null); // Include+ThenInclude
    Task<IEnumerable<T>> FindAllAsync(Expression<Func<T, bool>> criteria);
    Task<IEnumerable<T>> FindAllWithIncludes(Expression<Func<T, bool>>? criteria, params Expression<Func<T, object>>[] includes);
    Task<IEnumerable<T>> FindAllWithQueryIncludes(Expression<Func<T, bool>>? criteria = null, params Func<IQueryable<T>, IQueryable<T>>[] includes);
    Task<T?> FindTWithExpression(params Expression<Func<T, bool>>[] expressions);
    IQueryable<T> GetQueryable(Expression<Func<T, bool>>? criteria);   // ← أهم method: الكويري المعقد يتبني في السيرفس
}

// EF/Repositories/BaseRepository.cs
public class BaseRepository<T> : IBaseRepository<T> where T : class
{
    protected readonly ApplicationDbContext _context;
    public BaseRepository(ApplicationDbContext context) => _context = context;

    public async Task<T> AddAsync(T entity) { await _context.Set<T>().AddAsync(entity); return entity; }
    public async Task AddRangeAsync(IEnumerable<T> entities) => await _context.Set<T>().AddRangeAsync(entities);
    public T Update(T entity) { _context.Set<T>().Update(entity); return entity; }
    public void Delete(T entity) => _context.Set<T>().Remove(entity);
    public void DeleteRange(IEnumerable<T> entities) => _context.Set<T>().RemoveRange(entities);
    public async Task<IEnumerable<T>> GetAllAsync() => await _context.Set<T>().ToListAsync();
    public async Task<T?> GetByIdAsync(int id) => await _context.Set<T>().FindAsync(id);

    public async Task<T?> FindTWithIncludes(int id, string keyName, params Expression<Func<T, object>>[] includes)
    {
        IQueryable<T> q = _context.Set<T>();
        foreach (var inc in includes) q = q.Include(inc);
        return await q.FirstOrDefaultAsync(e => EF.Property<int>(e, keyName) == id);
    }
    public async Task<T?> FindTWithQueryIncludes(int id, string keyName, Func<IQueryable<T>, IQueryable<T>>? includes = null)
    {
        IQueryable<T> q = _context.Set<T>();
        if (includes != null) q = includes(q);
        return await q.FirstOrDefaultAsync(e => EF.Property<int>(e, keyName) == id);
    }
    public virtual async Task<IEnumerable<T>> FindAllAsync(Expression<Func<T, bool>> criteria) => await _context.Set<T>().Where(criteria).ToListAsync();
    public async Task<IEnumerable<T>> FindAllWithIncludes(Expression<Func<T, bool>>? criteria, params Expression<Func<T, object>>[] includes)
    {
        IQueryable<T> q = _context.Set<T>();
        if (criteria != null) q = q.Where(criteria);
        foreach (var inc in includes) q = q.Include(inc);
        return await q.ToListAsync();
    }
    public async Task<IEnumerable<T>> FindAllWithQueryIncludes(Expression<Func<T, bool>>? criteria = null, params Func<IQueryable<T>, IQueryable<T>>[] includes)
    {
        IQueryable<T> q = _context.Set<T>();
        if (criteria != null) q = q.Where(criteria);
        foreach (var inc in includes) q = inc(q);
        return await q.ToListAsync();
    }
    public async Task<T?> FindTWithExpression(params Expression<Func<T, bool>>[] expressions)
    {
        IQueryable<T> q = _context.Set<T>();
        foreach (var ex in expressions) q = q.Where(ex);
        return await q.FirstOrDefaultAsync();
    }
    public IQueryable<T> GetQueryable(Expression<Func<T, bool>>? criteria) => criteria == null ? _context.Set<T>() : _context.Set<T>().Where(criteria);
}

// Specific repo = سطر واحد، ولو احتجت كويري خاص بالـ entity تضيفه هنا
public interface IProjectRepository : IBaseRepository<Project> { }
public class ProjectRepository : BaseRepository<Project>, IProjectRepository
{ public ProjectRepository(ApplicationDbContext context) : base(context) { } }
```

## 2) Unit of Work (KMG)

```csharp
public interface IUnitOfWork
{
    IProjectRepository Project { get; }
    IClientRepository Client { get; }
    // ... property لكل repository
    Task<IDbContextTransaction> BeginTransactionAsync();
    Task<int> CompleteAsync();      // = SaveChangesAsync
    Task CommitAsync();
    Task RollbackAsync();
}

public class UnitOfWork : IUnitOfWork
{
    private readonly ApplicationDbContext _context;
    private IDbContextTransaction? _transaction;
    public IProjectRepository Project { get; }
    public IClientRepository Client { get; }

    public UnitOfWork(ApplicationDbContext context)
    {
        _context = context;
        Project = new ProjectRepository(context);   // كل الريبوز شايلين نفس الـ context ← نفس الـ transaction
        Client  = new ClientRepository(context);
    }

    public async Task<IDbContextTransaction> BeginTransactionAsync() => _transaction = await _context.Database.BeginTransactionAsync();
    public Task<int> CompleteAsync() => _context.SaveChangesAsync();
    public async Task CommitAsync()   { if (_transaction != null) { await _transaction.CommitAsync();   await _transaction.DisposeAsync(); _transaction = null; } }
    public async Task RollbackAsync() { if (_transaction != null) { await _transaction.RollbackAsync(); await _transaction.DisposeAsync(); _transaction = null; } }
}
// Program.cs → builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();   (Scoped = نفس الـ DbContext طول الـ request)
```

> تحسين اختياري للمشاريع الكبيرة: بدل ما الـ constructor يبني 40 repo في كل request، استخدم lazy (`private IProjectRepository? _project; public IProjectRepository Project => _project ??= new ProjectRepository(_context);`).

## 3) Query Optimization — القواعد

1. **القراءة: `AsNoTracking()`**، وفي القراءة التقيلة اعمل `Select` مباشرة للـ DTO بدل `Include`. الـ projection بيجيب الأعمدة المطلوبة بس ومفيهوش tracking.
   ```csharp
   var list = await _uow.Order.GetQueryable(o => o.RestaurantId == rid)
       .AsNoTracking()
       .OrderByDescending(o => o.CreatedAt)
       .Select(o => new OrderListDTO { Id = o.Id, Number = o.OrderNumber, Customer = o.Customer.Name, Total = o.Total })
       .ToListAsync();
   ```
2. **ابني الكويري خطوة بخطوة على `IQueryable`** (Elwasyet/Shifa) وماتعملش `ToList` غير في الآخر:
   ```csharp
   var q = _uow.Order.GetQueryable(null);
   if (filter.BranchId.HasValue) q = q.Where(o => o.BranchID == filter.BranchId);
   if (filter.StatusIds?.Any() == true) q = q.Where(o => filter.StatusIds.Contains(o.CurrentStatusId));
   if (!string.IsNullOrEmpty(filter.Search)) q = q.Where(o => o.OrderCode.Contains(filter.Search) || o.Client.Name.Contains(filter.Search));
   ```
3. **الـ predicates لازم تبقى sargable** علشان الـ index يشتغل:
   - ❌ `a.ActionDate.Date >= start.Date` أو `o.OrderDateTime.ToString().Contains(...)` (فانكشن على العمود = full scan)
   - ✅ `a.ActionDate >= start.Date && a.ActionDate < end.Date.AddDays(1)`
4. **تجنب N+1:** هات الـ ids الأول، وبعدين كويري واحدة بـ `Contains` للعلاقات (زي Elwasyet: `orderIds` ← requests بـ `orderIds.Contains(...)`)، أو استخدم projection.
5. **Includes كتير على collections** → `AsSplitQuery()` علشان تتجنب الـ cartesian explosion.
6. **تعديل أو مسح جماعي** من غير ما تحمّل الصفوف:
   ```csharp
   await db.Patients.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.NoShowCount, p => p.NoShowCount + 1)); // atomic increment
   await db.Set<PushDevice>().Where(x => x.Endpoint == ep).ExecuteDeleteAsync();
   ```
   (`ExecuteUpdate` atomic في الداتابيز = مفيش race على العداد.)
7. **التجميعات (totals) تتحسب في الداتابيز** (`SumAsync`/`GroupBy().Select(...)`)، **مش** إنك تحمّل كل الصفوف وتحسب في الذاكرة.
   > ⚠️ تقرير الـ Daily Report في Elwasyet (`OrderService` ~ سطر 395) بيعمل `ToListAsync()` لكل السجلات وبعدين `Skip/Take` في الذاكرة علشان يحسب الـ totals. الصح: كويري للـ totals بـ `SumAsync` + كويري للصفحة بـ `Skip/Take` في الـ SQL.
8. **كاش للقراءات العامة** (Shifa): `AddOutputCache` بـ policies + tags، و`EvictByTagAsync("directory")` بعد أي write بيأثر عليها (جوه الـ `TransactionActionFilter` بعد الـ commit).
9. **Command timeout** للـ migrations على جداول كبيرة: `db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10))` وبعدين رجّعه.

## 4) Pagination (Elwasyet)

```csharp
public class PaginationDTO { public int PageSize { get; set; } = 100; public int PageNumber { get; set; } = 1; }   // PageSize = 0 → من غير paging (للتصدير)

public class PagedResultDTO<T>
{
    public List<T> Items { get; set; } = new();
    public int TotalRecords { get; set; }
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => PageSize == 0 ? 1 : (int)Math.Ceiling((double)TotalRecords / PageSize);
    public bool HasNextPage => PageNumber < TotalPages;
    public bool HasPreviousPage => PageNumber > 1;
}

// الترتيب الصح: Filter → Count → Order → Skip/Take → Include/Select → ToList
var total = await q.CountAsync();                          // على الكويري المفلترة قبل الـ includes
var page = q.OrderByDescending(x => x.Id);                 // ترتيب ثابت (unique) إجباري قبل Skip
if (p.PageSize > 0) page = page.Skip((p.PageNumber - 1) * p.PageSize).Take(p.PageSize);
var items = await page.AsNoTracking().Select(o => new OrderReportDTO { ... }).ToListAsync();
return new PagedResultDTO<OrderReportDTO> { Items = items, TotalRecords = total, PageNumber = p.PageNumber, PageSize = p.PageSize };
```

- حط **حد أقصى** للـ PageSize في السيرفس (مثلاً 200) علشان محدش يطلب مليون صف.
- للجداول الضخمة جداً و"infinite scroll": **keyset pagination** (`Where(x => x.Id < lastId).OrderByDescending(x => x.Id).Take(n)`) أسرع من `Skip` في الصفحات البعيدة.

## 5) Indexing — إزاي نختار الـ indexes

القاعدة: **كل عمود بيتعمل عليه filter أو join أو order بشكل متكرر لازم يبقى عليه index، والـ composite index يبدأ بالـ tenant id.**

```csharp
// NED-Order: tenant أول عمود، بعده الفلاتر الشائعة، بعده الترتيب
b.Entity<Order>().HasIndex(o => new { o.RestaurantId, o.Status });
b.Entity<Order>().HasIndex(o => new { o.RestaurantId, o.CreatedAt });
b.Entity<Order>().HasIndex(o => new { o.RestaurantId, o.BranchId, o.Status, o.CreatedAt });
b.Entity<Product>().HasIndex(p => new { p.CategoryId, p.IsActive, p.DisplayOrder });   // منيو مرتب

// Elwasyet: indexes بأسماء صريحة على أعمدة الفلترة/الـ join
modelBuilder.Entity<Order>().HasIndex(o => o.ClientID).HasDatabaseName("IX_Order_ClientID");
modelBuilder.Entity<Order>().HasIndex(o => o.OrderDateTime).HasDatabaseName("IX_Order_OrderDateTime");
modelBuilder.Entity<Client>().HasIndex(c => c.Name).HasDatabaseName("IX_Client_Name");
modelBuilder.Entity<ApplicationUser>().HasIndex(c => c.PhoneNumber).HasDatabaseName("IX_User_PhoneNumber");

// Shifa: unique + filtered = business rule في الداتابيز
b.Entity<Drug>().HasIndex(d => d.Key).IsUnique().HasFilter("[Key] IS NOT NULL");
b.Entity<Appointment>().HasIndex(a => new { a.ClinicId, a.Date, a.Time }).IsUnique()
    .HasFilter("[Status] IN ('Pending','AwaitingPayment','ProofUploaded','Confirmed','CheckedIn','InSession','Completed')");
b.Entity<Drug>().Property(d => d.Brand).HasMaxLength(200);   // nvarchar(max) ماينفعش عليه index ← حدد MaxLength
```

- `string` اللي هيتعمل عليه index لازم يبقى عليه `HasMaxLength`.
- Spatial: `HasColumnType("geography")` + spatial index في الـ migration بـ raw SQL (Shifa).
- `Contains("x")` في البحث = `LIKE '%x%'` = مفيش index. لو البحث تقيل: عمود `Search` normalized + `StartsWith`، أو Full-Text Search.
- راجع الـ indexes بعد أي feature جديدة فيها فلتر جديد.
