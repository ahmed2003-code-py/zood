# Design Patterns — اللي بنستخدمه بشكل دوري ومكانه في الكود

## 1) Singleton (عن طريق الـ DI، مش static instance)

في ASP.NET Core الـ singleton بيتعمل بـ `AddSingleton`، مش بـ `private static Instance`. الـ container هو اللي بيضمن إن فيه نسخة واحدة.

| الـ Lifetime | امتى | أمثلة من مشاريعنا |
|---|---|---|
| **Singleton** | stateless، أو state مشتركة thread-safe، أو تقيلة في البناء | `Presence` (مين أونلاين، `ConcurrentDictionary`)، `PushQueue` (`Channel`)، `AiRateLimiter`، `ISecretProtector`، `IAmazonS3` client، `IAuthorizationPolicyProvider`، options objects |
| **Scoped** (الافتراضي) | أي حاجة بتلمس `DbContext` أو المستخدم الحالي | كل الـ Services، `IUnitOfWork`، `ICurrentUser`، `NotificationService` (عنده outbox per request)، filters |
| **Transient** | خفيفة ومن غير state | helpers صغيرة، validators |

```csharp
// Shifa
builder.Services.AddSingleton<Presence>();
builder.Services.AddSingleton<PushQueue>();
builder.Services.AddSingleton(builder.Configuration.GetSection("Ai").Get<AiOptions>() ?? new AiOptions());   // options كـ singleton instance

public class Presence   // thread-safe لأنه singleton وبيتنادى من كل الـ connections
{
    private readonly ConcurrentDictionary<string, (Guid User, string Role)> _connections = new();
    public void Connected(string connectionId, Guid user, string role) => _connections[connectionId] = (user, role);
    public void Disconnected(string connectionId) => _connections.TryRemove(connectionId, out _);
    public Dictionary<string, int> OnlineByRole() =>
        _connections.Values.DistinctBy(v => v.User).GroupBy(v => v.Role).ToDictionary(g => g.Key, g => g.Count());
}
```

**قواعد:**
- ⛔ **Captive dependency:** الـ singleton مايتحقنش فيه scoped (`DbContext`, `IUnitOfWork`, `ICurrentUser`). لو محتاج داتابيز: `IServiceScopeFactory` واعمل scope لكل وحدة شغل:
  ```csharp
  public class PushSender(PushQueue queue, IServiceScopeFactory scopes) : BackgroundService
  {
      protected override async Task ExecuteAsync(CancellationToken ct)
      {
          await foreach (var msg in queue.ReadAll(ct))
          {
              using var scope = scopes.CreateScope();
              var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
              // ...
          }
      }
  }
  ```
- أي state جوه singleton لازم تبقى thread-safe (`ConcurrentDictionary`، `Channel`، `Interlocked`، `lock`).
- الـ singleton state بيعيش في **الذاكرة بتاعة السيرفر ده بس**. لو هيبقى فيه أكتر من instance (scale-out) تبقى Redis/DB.
- الـ `static` cache (زي `PushKeys._cache` في Shifa) مقبول للقيم اللي مابتتغيرش طول عمر البروسس.

## 2) Service Provider / Dependency Injection

- كل الاعتمادات في الـ constructor (primary constructors في .NET 8: `public class BookingService(AppDbContext db, ICurrentUser user, ...)`).
- التسجيل متجمع في extension methods (`AddApplicationServices()` / `AddRepositories()`).
- **Typed HttpClient** لأي integration خارجي: `services.AddHttpClient<IDatatransPaymentService, DatatransPaymentService>(c => c.Timeout = TimeSpan.FromSeconds(20));` أو named: `AddHttpClient("ai", ...)` + `IHttpClientFactory`.
- **Options pattern:** `builder.Services.Configure<JWT>(builder.Configuration.GetSection("JWT"));` ← `IOptions<JWT>`.
- **Resolve من غير constructor** (middleware أو startup): `ctx.RequestServices.GetRequiredService<ICurrentUser>()`، أو `using var scope = app.Services.CreateScope();` للـ migrate والـ seed.
- **كذا implementation لنفس الـ interface:** سجّلهم كلهم، واحقن `IEnumerable<IInterface>` (شوف §3).

## 3) Factory + Strategy (NED-Order Promos، NedView Storage)

**Strategy:** كل implementation بيعمل نفس الحاجة بطريقة مختلفة. **Factory:** بتختار الـ strategy المناسبة وقت التشغيل.

```csharp
public interface IPromoConfigValidator
{
    PromoType Type { get; }          // كل strategy بتعلن هي لأنهي نوع
    Result Validate(string json);
}

public class BuyXGetYValidator : IPromoConfigValidator { public PromoType Type => PromoType.BuyXGetY; public Result Validate(string json) { /* ... */ } }
public class CartDiscountValidator : IPromoConfigValidator { public PromoType Type => PromoType.CartDiscount; /* ... */ }

public interface IPromoValidatorFactory { Result<IPromoConfigValidator> GetValidator(PromoType type); }

public class PromoValidatorFactory(IEnumerable<IPromoConfigValidator> validators) : IPromoValidatorFactory
{
    public Result<IPromoConfigValidator> GetValidator(PromoType type)
    {
        var v = validators.FirstOrDefault(x => x.Type == type);
        return v is null ? Result<IPromoConfigValidator>.Fail($"No validator found for type {type}") : Result<IPromoConfigValidator>.Success(v);
    }
}

// DI: كلهم على نفس الـ interface
services.AddScoped<IPromoConfigValidator, BuyXGetYValidator>();
services.AddScoped<IPromoConfigValidator, CartDiscountValidator>();
services.AddScoped<IPromoValidatorFactory, PromoValidatorFactory>();

// السيرفس
var v = _factory.GetValidator(dto.Type); if (!v.IsSuccess) return Result.Fail(v.Message!);
var check = v.Data!.Validate(dto.ConfigJson);
```
نوع جديد = class جديدة + سطر DI. مفيش `switch` بيكبر (Open/Closed).

**Strategy بالـ config (NedView):** `IVideoStorageService` ليه implementation اسمه `LocalVideoStorageService` وواحد تاني `DigitalOceanSpacesService`، والـ `Program.cs` بيسجل واحد بس منهم. ولو عايز التبديل يبقى من الـ appsettings:
```csharp
if (builder.Configuration["Storage:Provider"] == "S3") services.AddScoped<IVideoStorageService, DigitalOceanSpacesService>();
else services.AddScoped<IVideoStorageService, LocalVideoStorageService>();
```
نفس الفكرة في NedSwiss للـ AI (`OpenAiChatClient` / `GeminiClient`)، وفي KMG (`IAiChatClient` ← `GeminiChatClient`).

**Rule chain (NED-Order pricing):** `IOrderPromotionRule.ApplyAsync(PromotionContext ctx)`. كل rule بتعدّل في الـ context بالترتيب (Chain of Responsibility / Pipeline).

## 4) Builder

مفيش عندنا class اسمها `XBuilder` مكتوبة بإيدينا. الـ Builder موجود بالشكل ده:
- **Framework builders:** `WebApplication.CreateBuilder` → `builder.Services...` → `builder.Build()`، و`AuthorizationPolicyBuilder` (KMG: `new AuthorizationPolicyBuilder().AddRequirements(new AbilityRequirement(name)).Build()`)، و`ModelBuilder` fluent config، وQuestPDF fluent documents.
- **Query builder:** بناء `IQueryable` خطوة بخطوة بشروط (Elwasyet filters)، وده أكتر Builder بنستخدمه.

**Template لما نحتاج Builder مكتوب بإيدينا** (object معقد بقيم اختيارية كتير، زي notification أو PDF request أو test data):
```csharp
public sealed class NotificationBuilder
{
    private readonly Notification _n = new();
    public NotificationBuilder To(Guid userId) { _n.UserId = userId; return this; }
    public NotificationBuilder Title(string t) { _n.Title = t; return this; }
    public NotificationBuilder Body(string b) { _n.Body = b; return this; }
    public NotificationBuilder Link(string? l) { _n.Link = l; return this; }
    public NotificationBuilder Icon(string i) { _n.Icon = i; return this; }
    public Notification Build()
    {
        if (_n.UserId == Guid.Empty || string.IsNullOrWhiteSpace(_n.Title)) throw new InvalidOperationException("UserId and Title are required");
        return _n;
    }
}
// new NotificationBuilder().To(uid).Title("تم تأكيد حجزك").Link($"/booking/{id}").Build();
```
استخدمه لو الـ constructor هيبقى فيه أكتر من 4-5 باراميترز اختيارية، أو لو محتاج validation قبل ما الـ object يتبني. وفي التيستات: `TestDataBuilder` للـ entities.

## 5) State Machine (Shifa)

```csharp
public static class BookingStateMachine
{
    private static readonly Dictionary<AppointmentStatus, AppointmentStatus[]> Allowed = new()
    {
        [AppointmentStatus.Pending]         = [AppointmentStatus.Confirmed, AppointmentStatus.AwaitingPayment, AppointmentStatus.Rejected, AppointmentStatus.Cancelled],
        [AppointmentStatus.AwaitingPayment] = [AppointmentStatus.ProofUploaded, AppointmentStatus.Expired, AppointmentStatus.Cancelled, AppointmentStatus.Confirmed],
        [AppointmentStatus.ProofUploaded]   = [AppointmentStatus.Confirmed, AppointmentStatus.AwaitingPayment, AppointmentStatus.Cancelled],
        [AppointmentStatus.Confirmed]       = [AppointmentStatus.CheckedIn, AppointmentStatus.InSession, AppointmentStatus.NoShow, AppointmentStatus.Cancelled],
        [AppointmentStatus.CheckedIn]       = [AppointmentStatus.InSession, AppointmentStatus.Completed, AppointmentStatus.NoShow],
        [AppointmentStatus.InSession]       = [AppointmentStatus.Completed],
    };
    public static bool CanMove(AppointmentStatus from, AppointmentStatus to) => Allowed.TryGetValue(from, out var next) && next.Contains(to);
}

// السيرفس
if (!BookingStateMachine.CanMove(a.Status, to)) return Result<Appointment>.Conflict($"مينفعش تحول الحجز من {a.Status} لـ {to}");
```
- pure static ومن غير EF، فيتعمله unit test بسهولة. ونفس الفكرة في `PharmacyOrderMachine`.
- Elwasyet بيعمل نفس الفكرة بـ statuses في الداتابيز + `OperationLogService` + `OrderChangeTracker` للـ audit trail.

## 6) Producer / Consumer: شغل في الخلفية

```csharp
public class PushQueue   // Singleton
{
    private readonly Channel<PushMessage> _ch = Channel.CreateBounded<PushMessage>(new BoundedChannelOptions(5000) { FullMode = BoundedChannelFullMode.DropOldest });
    public void Enqueue(PushMessage m) => _ch.Writer.TryWrite(m);                         // الـ request مابيستناش
    public IAsyncEnumerable<PushMessage> ReadAll(CancellationToken ct) => _ch.Reader.ReadAllAsync(ct);
}
builder.Services.AddSingleton<PushQueue>();
builder.Services.AddHostedService<PushSender>();   // consumer (شوف §1)
```
- **`BackgroundService` + `PeriodicTimer`**: jobs دورية بسيطة (Shifa `HoldExpiryJob`، `ReminderJob`؛ NedView `ScreenCleanupService`).
- **Hangfire** (Elwasyet، CRM): لما محتاج persistence وretries وdashboard وcron (`SubscriptionReminderJob`).
- الـ job **idempotent**: unique index على مفتاح (`ReminderLog.Key` unique) علشان التذكير مايتبعتش مرتين لو الـ job اشتغل تاني.

## 7) Dynamic Policy Provider: صلاحيات من الداتابيز (KMG)

```csharp
public class AuthorizeAbilityAttribute : AuthorizeAttribute
{ public AuthorizeAbilityAttribute(string ability) => Policy = $"Ability:{ability}"; }

public class AbilityPolicyProvider(IOptions<AuthorizationOptions> options) : IAuthorizationPolicyProvider
{
    private const string Prefix = "Ability:";
    private readonly DefaultAuthorizationPolicyProvider _fallback = new(options);
    public Task<AuthorizationPolicy?> GetPolicyAsync(string name) =>
        name.StartsWith(Prefix, StringComparison.Ordinal)
            ? Task.FromResult<AuthorizationPolicy?>(new AuthorizationPolicyBuilder().AddRequirements(new AbilityRequirement(name[Prefix.Length..])).Build())
            : _fallback.GetPolicyAsync(name);
    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();
    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();
}

public class AbilityAuthorizationHandler(IUnitOfWork uow) : AuthorizationHandler<AbilityRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext ctx, AbilityRequirement req)
    {
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier); if (userId is null) return;
        var direct = await uow.UserAbility.GetQueryable(u => u.UserId == userId).Select(u => u.Ability.AbilityName).ToListAsync();
        var roles  = await uow.UserRole.GetQueryable(r => r.UserId == userId).Select(r => r.RoleId).ToListAsync();
        var viaRole = await uow.RolesAbility.GetQueryable(ra => roles.Contains(ra.RoleId)).Select(ra => ra.Ability.AbilityName).ToListAsync();
        if (direct.Union(viaRole).Contains(req.AbilityName)) ctx.Succeed(req);
    }
}
// Program.cs
builder.Services.AddScoped<IAuthorizationHandler, AbilityAuthorizationHandler>();
builder.Services.AddSingleton<IAuthorizationPolicyProvider, AbilityPolicyProvider>();
// Controller
[HttpPost, AuthorizeAbility("إدارة العملاء")]
```
تحسين للمشاريع الكبيرة: كاش للـ abilities per user (`IMemoryCache` لمدة قصيرة) بدل 3 كويريز في كل request.

## 8) Decorator عن طريق Filters و Middleware

الـ cross-cutting مابيتكتبش جوه السيرفسيس. بيتلف حواليها:
- `TransactionActionFilter` (transaction)، و`IdempotencyFilter` (dedupe)، و`ApiKeyAuthFilter` (KMG: endpoints للأنظمة الخارجية)، و`GlobalExceptionMiddleware`، و`JwtCookieMiddleware` (NedView)، و`UseClinicMembershipCheck` (Shifa).
- `TypeFilterAttribute` علشان الفلتر ياخد dependencies من الـ DI (`IdempotentAttribute : TypeFilterAttribute`).
