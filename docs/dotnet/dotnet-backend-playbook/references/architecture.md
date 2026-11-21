# Architecture — الهيكل والطبقات وإزاي بيتكلموا بعض

## 1) الـ 3-Tier بالتفصيل

```
MyApp.sln
├─ MyApp.Api/                       (ASP.NET Core Web API — بيعتمد على Core و EF)
│  ├─ Controllers/                  رفيعة: DTO داخل ← Service ← Result ← HTTP
│  ├─ Authorization/                AbilityPolicyProvider, handlers, ApiKeyAuthFilter
│  ├─ Filters/ أو Helper/           TransactionActionFilter, IdempotencyFilter, GlobalExceptionMiddleware
│  ├─ Helper/ServiceExtensions.cs   AddApplicationServices()
│  ├─ Helper/RepositoryExtensions.cs AddRepositories()
│  ├─ Helper/DbInitializer.cs       Migrate + Seed (roles/admin)
│  └─ Program.cs
├─ MyApp.Core/                      (مابيعتمدش على EF)
│  ├─ Models/                       Entities
│  ├─ DTOs/<Feature>/               Request/Response DTOs
│  ├─ Enums/
│  ├─ Interfaces/                   IBaseRepository<T>, I<X>Repository, IUnitOfWork
│  ├─ Interfaces/Services/          I<X>Service
│  ├─ Services/                     <X>Service : I<X>Service  (Business logic)
│  └─ Helper/                       Result<T>, TimeHelper, JWT options, ClaimsExtensions
└─ MyApp.EF/                        (بيعتمد على Core)
   ├─ Data/ApplicationDbContext.cs
   ├─ Repositories/                 BaseRepository<T>, <X>Repository, UnitOfWork
   └─ Migrations/
```

> ملاحظة: KMG وNED-Order بيحطوا الـ Services جوه Core (مع إن Core مابيعتمدش على EF، السيرفس بتستخدم `IQueryable` + extension methods زي `Include` و`ToListAsync`، فبيبقى فيه package reference لـ `Microsoft.EntityFrameworkCore` في Core). ده الـ trade-off المقبول عندنا: بيكسبنا composition للكويري جوه السيرفس.

## 2) DI registration بـ extension methods (NED-Order)

```csharp
// MyApp.Api/Helper/ServiceExtensions.cs
public static class ServiceExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IOrderService, OrderService>();
        // ...
        services.AddSingleton<ISecretProtector, SecretProtector>();             // stateless → singleton
        services.AddHttpClient<IDatatransPaymentService, DatatransPaymentService>(c => c.Timeout = TimeSpan.FromSeconds(20)); // typed client

        // Strategy family: كذا implementation لنفس الـ interface + factory بتختار
        services.AddScoped<IPromoConfigValidator, BuyXGetYValidator>();
        services.AddScoped<IPromoConfigValidator, CartDiscountValidator>();
        services.AddScoped<IPromoValidatorFactory, PromoValidatorFactory>();
        return services;
    }
}

public static class RepositoryExtensions
{
    public static IServiceCollection AddRepositories(this IServiceCollection services)
    {
        services.AddScoped<IUnitOfWork, UnitOfWork>();   // الـ UoW بيبني الريبوز بنفسه
        return services;
    }
}

// Program.cs
builder.Services.AddDbContext<ApplicationDbContext>(o => o.UseSqlServer(cs));
builder.Services.AddRepositories().AddApplicationServices();
builder.Services.AddScoped<TransactionActionFilter>();
builder.Services.AddControllers(o => o.Filters.AddService<TransactionActionFilter>(order: 0));
```

## 3) Result Pattern (موحّد في NED-Order وShifa)

```csharp
public class Result
{
    public bool IsSuccess { get; protected set; }
    public string? Message { get; protected set; }
    public int StatusCode { get; protected set; }
    public List<string>? Errors { get; protected set; }

    public static Result Success(string? message = null) => new() { IsSuccess = true, Message = message, StatusCode = 200 };
    public static Result Fail(string message, int statusCode = 400, List<string>? errors = null) =>
        new() { IsSuccess = false, Message = message, StatusCode = statusCode, Errors = errors };
    public static Result NotFound(string message = "غير موجود") => Fail(message, 404);
    public static Result Conflict(string message) => Fail(message, 409);
    public static Result Forbidden(string message = "غير مسموح") => Fail(message, 403);
}

public class Result<T> : Result
{
    public T? Data { get; private set; }
    public static Result<T> Success(T data, string? message = null) => new() { IsSuccess = true, Data = data, Message = message, StatusCode = 200 };
    public new static Result<T> Fail(string message, int statusCode = 400, List<string>? errors = null) =>
        new() { IsSuccess = false, Message = message, StatusCode = statusCode, Errors = errors };
    public new static Result<T> NotFound(string message = "غير موجود") => Fail(message, 404);
    public new static Result<T> Conflict(string message) => Fail(message, 409);
    public new static Result<T> Forbidden(string message = "غير مسموح") => Fail(message, 403);

    public static implicit operator Result<T>(T data) => Success(data);   // return entity; بدل Result<T>.Success(entity)
}
```

### Base controller بيحول الـ Result لـ HTTP (Shifa `Common/Web.cs`)

```csharp
[ApiController]
[Route("api/v1/[controller]")]
public abstract class ApiController : ControllerBase
{
    protected IActionResult From(Result r) => r.IsSuccess
        ? Ok(new { success = true, message = r.Message })
        : StatusCode(r.StatusCode, new { success = false, message = r.Message, errors = r.Errors });

    protected IActionResult From<T>(Result<T> r) => r.IsSuccess
        ? Ok(r.Data)
        : StatusCode(r.StatusCode, new { success = false, message = r.Message, errors = r.Errors });
}

// استخدام
[HttpPost("book"), Authorize(Roles = Roles.Patient), Idempotent]
public async Task<IActionResult> Book(BookRequest req) => From(await booking.Book(...));
```

> مهم: الـ status code اللي بيرجع من `From` هو اللي الـ `TransactionActionFilter` بيقرر عليه commit أو rollback. يعني `Result.Fail` لوحده كفاية إنه يلغي أي حاجة اتكتبت في الـ request.

## 4) Service ← Service ← UnitOfWork

```csharp
public class StockService : IStockService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICashBoxService _cashBoxService;   // سيرفس بتكلم سيرفس بالـ interface

    public StockService(IUnitOfWork unitOfWork, ICashBoxService cashBoxService)
    { _unitOfWork = unitOfWork; _cashBoxService = cashBoxService; }

    public async Task<int> CreateMaterialAsync(CreateMaterialDTO model, int? employeeId)
    {
        using var tx = await _unitOfWork.BeginTransactionAsync();   // يدوي — لو المشروع مفيهوش TransactionActionFilter
        try
        {
            var material = new Material { Name = model.Name, Quantity = model.InitialQuantity, LastUpdated = TimeHelper.NowInEgypt };
            await _unitOfWork.Material.AddAsync(material);
            await _unitOfWork.CompleteAsync();                       // محتاج material.Id
            if (model.InitialQuantity > 0)
                await _unitOfWork.StockMovement.AddAsync(new StockMovement { MaterialId = material.Id, MovementType = MovementType.OpeningBalance, Quantity = model.InitialQuantity });
            await _unitOfWork.CompleteAsync();
            await _unitOfWork.CommitAsync();
            return material.Id;
        }
        catch { await _unitOfWork.RollbackAsync(); throw; }
    }
}
```

القاعدة: **لو الـ `TransactionActionFilter` متسجل، ماتفتحش transaction يدوي في السيرفس** (الفلتر بيتخطى لو فيه `CurrentTransaction` موجودة أصلاً، بس الأنضف إن السيرفس ماتعرفش حاجة عن الـ transactions).

## 5) Global Exception Middleware (Shifa — أكمل نسخة)

```csharp
public class GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
{
    public async Task Invoke(HttpContext ctx)
    {
        try { await next(ctx); }
        catch (DbUpdateConcurrencyException)                                   // rowversion اتغير
        { await Write(ctx, HttpStatusCode.Conflict, "البيانات اتغيرت من مستخدم تاني — حدّث الصفحة وحاول تاني"); }
        catch (Exception ex) when (IsDeadlock(ex))                             // 1205 deadlock / 1222 lock timeout
        { await Write(ctx, HttpStatusCode.Conflict, "فيه عملية تانية اتعملت في نفس اللحظة — جرّب تاني"); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) // unique index
        { await Write(ctx, HttpStatusCode.Conflict, "العملية دي اتعملت قبل كده"); }
        catch (UnauthorizedAccessException ex)                                 // cross-tenant write
        { logger.LogWarning(ex, "Blocked cross-tenant access on {Path}", ctx.Request.Path); await Write(ctx, HttpStatusCode.Forbidden, "غير مسموح"); }
        catch (Exception ex)
        { logger.LogError(ex, "Unhandled error on {Path}", ctx.Request.Path); await Write(ctx, HttpStatusCode.InternalServerError, "حصلت مشكلة غير متوقعة"); }
    }

    private static bool IsDeadlock(Exception? ex)
    { for (; ex is not null; ex = ex.InnerException) if (ex is SqlException { Number: 1205 or 1222 }) return true; return false; }

    private static Task Write(HttpContext ctx, HttpStatusCode code, string message)
    {
        if (ctx.Response.HasStarted) return Task.CompletedTask;
        ctx.Response.StatusCode = (int)code; ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(new { success = false, message }));
    }
}
// app.UseMiddleware<GlobalExceptionMiddleware>();  ← أول حاجة في الـ pipeline
```

## 6) ترتيب الـ pipeline (Shifa)

```csharp
app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseResponseCompression();
app.UseSerilogRequestLogging();
app.UseSwagger(); app.UseSwaggerUI();
app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();          // بعد الـ auth علشان يقسم per-user
// custom membership checks (tenant membership) هنا
app.UseAuthorization();
app.UseOutputCache();
app.MapControllers();
app.MapHub<LiveHub>("/hubs/live");
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
```

## 7) DbContext conventions (Shifa)

```csharp
protected override void ConfigureConventions(ModelConfigurationBuilder b)
{
    b.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);   // enums مقروءة في الداتابيز
    b.Properties<decimal>().HavePrecision(12, 2);                      // مفيش warning ولا truncation
}

public override Task<int> SaveChangesAsync(CancellationToken ct = default)
{
    foreach (var e in ChangeTracker.Entries<BaseEntity>().Where(e => e.State == EntityState.Modified))
        e.Entity.UpdatedAt = DateTime.UtcNow;                            // audit fields أوتوماتيك
    // + tenant write guard (شوف multi-tenancy.md)
    return base.SaveChangesAsync(ct);
}
```

## 8) Modular Monolith (Shifa) — امتى وإزاي

- مشروع واحد `Shifa.Api`: `Common/` (Result, BaseEntity, ICurrentUser, filters, middleware)، و`Infrastructure/` (DbContext, seeders, jobs)، و`Modules/<Feature>/<Feature>.cs` (controller + service + request records في نفس الملف).
- `Modules/Domain.cs` فيه الـ entities مجمعة حسب الموديول، و`Modules/Rules.cs` فيه **pure rules** (state machine، slot calculator، ranking) عليها tests في `tests/Shifa.Tests`.
- السيرفس بتاخد `ShifaDbContext` مباشرة. الـ global filters والـ `TransactionActionFilter` بيدّوا نفس الضمانات اللي الـ UoW بيديها.
- مناسب لما الدومين كبير والسرعة مهمة. ولو الموديول كبر، يتفصل لمشروع بسهولة لأن حدوده واضحة.
