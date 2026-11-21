# Concurrency & Idempotency — حل الـ Race Conditions والطلبات المكررة

المصدر: Shifa (`Common/Idempotency.cs`، `Common/Web.cs`، `Modules/Booking/Booking.cs`، `Infrastructure/ShifaDbContext.cs`) + NED-Order (`NEDOrder.EF/Filter/TransactionActionFilter.cs`).

الضمانات متركبة طبقات، وكل طبقة بتغطي حاجة:

| الطبقة | بتمنع إيه |
|---|---|
| Transaction لكل request | نص العملية يتحفظ ونصها لأ |
| Idempotency-Key | الـ retry أو الدبل كليك ينفذ العملية مرتين |
| App lock (`sp_getapplock`) | اتنين يحجزوا نفس الميعاد في نفس اللحظة |
| Re-check بعد الـ lock | قرار مبني على داتا قديمة |
| Unique / filtered unique index | أي حاجة عدّت من اللي فوق (خط الدفاع الأخير) |
| `rowversion` | آخر واحد يكتب يمسح شغل اللي قبله (lost update) |
| Exception → 409 | المستخدم يشوف 500 بدل "حدّث وجرب تاني" |

## 1) TransactionActionFilter: transaction واحدة لكل write request

```csharp
public class TransactionActionFilter(AppDbContext db, ILogger<TransactionActionFilter> logger /*, NotificationService notify */) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var method = context.HttpContext.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || db.Database.CurrentTransaction is not null)
        { await next(); /* await notify.Flush(); */ return; }

        await using var tx = await db.Database.BeginTransactionAsync();
        var executed = await next();

        if (executed.Exception is not null && !executed.ExceptionHandled)
        { await tx.RollbackAsync(); /* notify.Discard(); */ return; }          // الـ exception تكمل للـ middleware

        var status = (executed.Result as ObjectResult)?.StatusCode ?? (executed.Result as StatusCodeResult)?.StatusCode ?? 200;
        if (status >= 400)
        { logger.LogInformation("{Action} returned {Status} — rolling back", context.ActionDescriptor.DisplayName, status); await tx.RollbackAsync(); /* notify.Discard(); */ return; }

        await db.SaveChangesAsync();
        await tx.CommitAsync();
        // await notify.Flush();           ← الإشعارات بعد الـ commit بس (§6)
        // await cache.EvictByTagAsync(...) ← تفريغ الكاش بعد الـ commit
    }
}
// Program.cs
builder.Services.AddScoped<TransactionActionFilter>();
builder.Services.AddControllers(o => o.Filters.AddService<TransactionActionFilter>(order: 0));   // order 0 = يلف حوالين باقي الفلاتر
```

## 2) Idempotency Key

العميل يبعت `Idempotency-Key: <uuid>` جديد لكل **action من المستخدم** (مش لكل request، والـ retry بيبعت نفس المفتاح).

| الحالة | الرد |
|---|---|
| أول مرة | تتنفذ ويتخزن الرد |
| نفس المفتاح ونفس الـ body وخلصت | يرجع الرد المتخزن **من غير ما تتنفذ تاني** |
| نفس المفتاح ولسه بتتنفذ (request موازي) | 409 |
| نفس المفتاح بـ body مختلف | 422 |
| التنفيذ فشل | rollback ← المفتاح يتحرر والعميل يقدر يعمل retry |

```csharp
public class IdempotencyRecord
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public string Key { get; set; } = "";
    public string Path { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public int? StatusCode { get; set; }          // null = لسه شغالة
    public string? ResponseJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
// OnModelCreating:
b.Entity<IdempotencyRecord>().HasIndex(r => new { r.UserId, r.Key }).IsUnique();   // ← ده اللي بيقفل الـ request الموازي

[AttributeUsage(AttributeTargets.Method)]
public class IdempotentAttribute : TypeFilterAttribute
{ public IdempotentAttribute() : base(typeof(IdempotencyFilter)) { Order = 10; } }   // بعد الـ TransactionActionFilter (order 0) ← جوه الـ transaction

public class IdempotencyFilter(AppDbContext db, ICurrentUser user) : IAsyncActionFilter
{
    public const string Header = "Idempotency-Key";

    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        var key = ctx.HttpContext.Request.Headers[Header].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100)
        { ctx.Result = new BadRequestObjectResult(new { success = false, message = $"الهيدر {Header} مطلوب" }); return; }

        var userId = user.UserId ?? Guid.Empty;
        var path = ctx.HttpContext.Request.Path.Value ?? "";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path + JsonSerializer.Serialize(ctx.ActionArguments))));

        var existing = await db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r => r.UserId == userId && r.Key == key);
        if (existing is not null)
        {
            if (existing.RequestHash != hash) ctx.Result = new UnprocessableEntityObjectResult(new { success = false, message = "المفتاح ده اتستخدم مع طلب مختلف" });
            else if (existing.StatusCode is null) ctx.Result = new ConflictObjectResult(new { success = false, message = "الطلب ده لسه بيتنفذ" });
            else ctx.Result = new ContentResult { StatusCode = existing.StatusCode, Content = existing.ResponseJson, ContentType = "application/json" };
            return;
        }

        var record = new IdempotencyRecord { UserId = userId, Key = key, Path = path, RequestHash = hash };
        db.IdempotencyRecords.Add(record);
        await db.SaveChangesAsync();          // يحجز المفتاح جوه الـ transaction، والـ request الموازي يخبط في الـ unique index ← 409

        var executed = await next();
        if (executed.Result is ObjectResult obj && (obj.StatusCode ?? 200) < 400)
        {
            record.StatusCode = obj.StatusCode ?? 200;
            record.ResponseJson = JsonSerializer.Serialize(obj.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }   // الـ TransactionActionFilter هيعمل SaveChanges + Commit. ولو فشل هيعمل rollback ويتمسح السجل
    }
}

// Swagger: IOperationFilter بيضيف الهيدر required لأي action عليها [Idempotent]  (IdempotencyHeaderFilter في Shifa)
// الاستخدام:
[HttpPost, Idempotent] public async Task<IActionResult> Book(BookRequest req) => From(await booking.Book(...));
```

**امتى تحط `[Idempotent]`:** حجز، دفع أو رفع إثبات دفع، مراجعة إثبات، check-in وتحصيل، إنشاء order، صرف مرتب، أي حاجة فيها فلوس أو مخزون.
**تنظيف:** job دورية تمسح السجلات الأقدم من 24-48 ساعة.
**الفرونت:** `crypto.randomUUID()` لما المستخدم يدوس الزرار، وتفضل نفس القيمة في الـ retries لحد ما يجي رد نهائي.

## 3) App Lock: سيريالايز على مورد منطقي (`sp_getapplock`)

```csharp
/// Exclusive lock per clinic-day, released automatically with the request transaction.
private Task SlotLock(Guid clinicId, DateOnly date) =>
    db.Database.ExecuteSqlInterpolatedAsync(
        $"EXEC sp_getapplock @Resource = {"slot:" + clinicId + ":" + date.ToString("O")}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000");

public async Task<Result<Appointment>> Book(...)
{
    // 1) validations الرخيصة قبل الـ lock
    await SlotLock(clinicId, date);                                         // 2) lock (لازم جوه transaction ← الـ TransactionActionFilter)
    var slot = (await scheduling.Slots(clinic, date)).FirstOrDefault(s => s.Time == time);
    if (slot is null || !slot.Free) return Result<Appointment>.Conflict("الميعاد ده اتحجز — اختار ميعاد تاني");   // 3) re-check تحت الـ lock
    db.Appointments.Add(new Appointment { ... });
    await db.SaveChangesAsync();                                            // 4) الـ unique index هو الضمان الأخير
    return a;
}
```

- **الـ resource name** بيحدد الحبيبات (granularity): `slot:{clinic}:{date}` (يوم عيادة)، `inv:{pharmacyId}` (ترقيم فواتير الصيدلية).
- استخدمه لما القرار معتمد على **مجموعة صفوف** (هل الميعاد فاضي؟ إيه آخر رقم فاتورة؟) ومفيش صف واحد ينفع تعمله row lock.
- SQL Server بس. في PostgreSQL المقابل: `pg_advisory_xact_lock(hashtext(key))`.

## 4) Unique / Filtered Unique Index: الـ business rule جوه الداتابيز

```csharp
// ميعاد واحد "حي" لكل خانة في العيادة (الملغي ميمنعش حجز جديد)
b.Entity<Appointment>().HasIndex(a => new { a.ClinicId, a.Date, a.Time }).IsUnique()
    .HasFilter("[Status] IN ('Pending','AwaitingPayment','ProofUploaded','Confirmed','CheckedIn','InSession','Completed')");
// نفس التحويل البنكي مايتبعتش لحجزين
b.Entity<PaymentProof>().HasIndex(p => p.RefNo).IsUnique().HasFilter("[Decision] <> 'Rejected'");
b.Entity<PaymentProof>().HasIndex(p => p.ImageHash).IsUnique().HasFilter("[Decision] <> 'Rejected'");
// المرتب يتصرف مرة واحدة في الشهر حتى مع الدبل كليك
b.Entity<PayrollPayment>().HasIndex(x => new { x.StaffId, x.Month }).IsUnique();
// رقم الفاتورة فريد لكل صيدلية حتى مع البيع المتزامن
b.Entity<SaleInvoice>().HasIndex(x => new { x.PharmacyId, x.Number }).IsUnique();
```
الـ middleware بيحول SqlException 2601/2627 لـ 409 برسالة مفهومة.

## 5) Optimistic Concurrency بـ `rowversion`

```csharp
public interface IVersioned { byte[] Version { get; set; } }
b.Entity<Appointment>().Property(a => a.Version).IsRowVersion();
b.Entity<PharmacyStock>().Property(s => s.Version).IsRowVersion();   // اتنين كاشير بيبيعوا آخر علبة ← واحد ينجح والتاني 409
```
EF بيضيف `WHERE Version = @original` في الـ UPDATE. لو حد عدّل الصف قبلك يبقى `DbUpdateConcurrencyException`، والـ middleware يرجع 409 "البيانات اتغيرت — حدّث الصفحة".
أمثلة: المريض بيرفع إثبات دفع وفي نفس اللحظة الـ job بيلغي الحجز لأن المهلة خلصت؛ أو accept/decline على نفس طلب التغيير.

**بديل ذري من غير تحميل الصف:** `ExecuteUpdateAsync(s => s.SetProperty(p => p.Count, p => p.Count + 1))`. ولخصم مخزون بشرط: `Where(s => s.Id == id && s.Quantity >= qty).ExecuteUpdateAsync(...)`، ولو رجع 0 يبقى المخزون مش كفاية.

## 6) Outbox للإشعارات: مفيش إشعار قبل الـ commit

```csharp
public class NotificationService(AppDbContext db, IHubContext<LiveHub> hub, PushQueue push)
{
    private readonly List<(string Group, string Event, object Payload)> _outbox = [];

    public void ToUser(Guid userId, string title, string body)
    {
        db.Notifications.Add(new Notification { UserId = userId, Title = title, Body = body });   // المتخزن بيتحفظ مع نفس الـ transaction
        _outbox.Add(($"user:{userId}", "notification", new { title, body }));                    // اللايف بيستنى
    }
    public async Task Flush()   // TransactionActionFilter بينادي عليها بعد Commit
    {
        var items = _outbox.ToList(); _outbox.Clear();
        foreach (var (g, e, p) in items) { try { await hub.Clients.Group(g).SendAsync(e, p); } catch { /* best effort */ } }
    }
    public void Discard() => _outbox.Clear();   // بعد Rollback
}
```
ليه؟ لو الإشعار طلع قبل الـ commit، العميل هيعمل reload ومش هيلاقي الداتا، أو هيلاقي إشعار لعملية اتعملها rollback. نفس الكلام على تفريغ الكاش وإرسال الإيميلات.

## 7) Rate Limiting (Shifa)

```csharp
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c =>
    {
        if (c.Request.Path.StartsWithSegments("/api/v1/auth") && !c.Request.Path.StartsWithSegments("/api/v1/auth/refresh"))
            return RateLimitPartition.GetFixedWindowLimiter("auth:" + Ip(c), _ => new() { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) });
        var userId = c.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return userId is not null    // مستخدم مسجل: per-account (ناس كتير ورا IP واحد في مصر بسبب carrier NAT)
            ? RateLimitPartition.GetTokenBucketLimiter("u:" + userId, _ => new() { TokenLimit = 300, TokensPerPeriod = 30, ReplenishmentPeriod = TimeSpan.FromSeconds(1), AutoReplenishment = true })
            : RateLimitPartition.GetTokenBucketLimiter("ip:" + Ip(c), _ => new() { TokenLimit = 600, TokensPerPeriod = 60, ReplenishmentPeriod = TimeSpan.FromSeconds(1), AutoReplenishment = true });
    });
});
// app.UseRateLimiter() بعد UseAuthentication
```
+ Identity lockout (`MaxFailedAccessAttempts = 5`) ضد تخمين الباسورد per account.
