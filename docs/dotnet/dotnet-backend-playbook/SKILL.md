---
name: dotnet-backend-playbook
description: The house architecture, design patterns and engineering principles used across the user's ASP.NET Core projects (Shifa, NED-Order, KMG, Elwasyet, Nedx/CRMProject, NedSwiss, NedView). Use whenever starting a new .NET backend (small or large), adding a module/service/repository/controller to one, or when the work touches 3-tier layering, Generic Repository + Unit of Work, Result<T>, per-request transactions, idempotency keys, race conditions / double booking / concurrency (rowversion, sp_getapplock, unique indexes), multi-tenancy with global query filters, DI lifetimes (singleton/scoped), factory/strategy, state machines, query optimization, indexing or pagination. Also use when the user says "زي ما بنعمل في الشفا / نيد أوردر / KMG / الوسيط" or asks to follow "our structure".
---

# .NET Backend Playbook — الأركتكتشر والباترنز اللي بنشتغل بيها

الـ skill دي خلاصة الشغل في المشاريع. الهدف: أي مشروع جديد يتبني على نفس الهيكل ونفس الضمانات من غير ما نعيد التفكير من الأول.
الكود اللي هنا متاخد من المشاريع الحقيقية ومتنضف. لو محتاج الأصل كامل، المسارات في جدول المشاريع تحت.

## 1) خريطة المشاريع (المرجع لكل باترن)

| المشروع | المسار | الشكل | أهم حاجة تتاخد منه |
|---|---|---|---|
| **Shifa** (منصة طبية) | `C:\Users\Mostafa\Desktop\projects\Shifa\shifa-api\src\Shifa.Api` | Modular monolith (مشروع واحد مقسوم Modules) | **Idempotency**، **Global Query Filter للـ multi-tenant**، منع الـ race condition (`sp_getapplock` + filtered unique index + `rowversion`)، State machine، Outbox للإشعارات بعد الـ commit، Rate limiting، Output cache |
| **NED-Order** (SaaS مطاعم) | `C:\Users\Mostafa\source\repos\NED-Order` | 3 layers: `NED Order` (API) / `NEDOrder.Core` / `NEDOrder.EF` | `TransactionActionFilter` (الأصل)، `Result<T>`، تسجيل DI بـ extension methods، **Factory + Strategy** (Promo validators)، الـ tenant = `RestaurantId` من الـ JWT |
| **KMG** (ERP داخلي) | `C:\Users\Mostafa\Desktop\projects\KMG` | 3 layers: `KMG.Api` / `KMG.Core` / `KMG.EF` | **أنضف Generic Repository + UnitOfWork**، صلاحيات ديناميكية (`AbilityPolicyProvider`)، xUnit tests |
| **Elwasyet** (الوسيط) | `C:\Users\Mostafa\Desktop\projects\Elwasyet` | 3 layers: `Elwasyet.Api` / `Elwasyet.Core` / `Elwasyet.EF` | داتا كبيرة: Indexes، Pagination (`PagedResultDTO<T>`)، بناء الكويري خطوة بخطوة، Hangfire |
| **Nedx / CRMProject** | `C:\Users\Mostafa\Desktop\projects\CRMProject` | 3 layers: `.API` / `.CORE` / `.EF` | SaaS متعدد الشركات (tenant = `CompanyId` من الـ claim)، Hangfire jobs، Super admin |
| **NedSwiss** | `C:\Users\Mostafa\Desktop\projects\NedSwiss\NedSwiss` | MVC مشروع واحد (Controllers / Core / EF) | Session auth، AI assistant بمزوّدين قابلين للتبديل |
| **NedView** | `C:\Users\Mostafa\Desktop\projects\NedView\NedView` | MVC مشروع واحد | **Strategy** للتخزين (`IVideoStorageService`: Local ↔ DigitalOcean)، `BackgroundService`، SignalR |

كتالوج السيرفسيس الجاهزة (دفع، PDF، إيميل، SMS، إشعارات، Excel، AI…) موجود في
`C:\Users\Mostafa\Desktop\projects\ServicesCatalog\SERVICES_CATALOG.md`. ارجعله لما تحتاج **feature**. الـ skill دي للـ **structure والـ patterns**.

## 2) الهيكل الافتراضي: 3-Tier

```
<App>.Api      → Controllers, Filters, Middleware, Program.cs, DI extensions, Authorization handlers
<App>.Core     → Models (Entities), DTOs, Enums, Interfaces (IRepositories + IServices), Services (Business Logic), Helpers (Result<T>, ...)
<App>.EF       → ApplicationDbContext, Repositories (BaseRepository<T>, UnitOfWork), Migrations, Configurations
```

اتجاه الاعتماد: `Api → Core ← EF`. الـ Core مايعرفش حاجة عن EF. الـ interfaces جوه Core، والتنفيذ جوه EF.

**مسار الـ request:**
`Controller` (رفيع: ياخد DTO ويقرأ الـ claims ويرجع HTTP) → `Service` (كل الـ business rules، بيرجع `Result<T>`) → `IUnitOfWork.<Repo>` (data access) → `DbContext`.

- الـ Controller **مايكلمش** الـ DbContext ولا الـ repositories مباشرة.
- السيرفس بتكلم سيرفس تانية عن طريق الـ **interface** المحقون في الـ constructor (مثلاً `StockService` بيستخدم `ICashBoxService.ReverseAsync`)، مش `new`.
- السيرفس بتكلم الريبوز **من خلال `IUnitOfWork` بس**: repository واحد لكل entity، وكلهم شايلين نفس الـ `DbContext`، فـ `CompleteAsync()` واحدة بتحفظ كل حاجة مع بعض.

**امتى أخرج عن الـ 3-tier؟**
- مشروع صغير أو لوحة تحكم → MVC مشروع واحد بفولدرات `Controllers / Core / EF` (زي NedSwiss وNedView).
- منصة كبيرة بموديولات كتير وفريق صغير → **Modular monolith** زي Shifa: مشروع واحد، `Modules/<Feature>/` فيه الـ controller والـ service والـ DTOs (records)، والـ services بتستخدم الـ `DbContext` مباشرة (الـ DbContext نفسه هو الـ UoW، والـ `DbSet` هو الـ repository). والـ cross-cutting كله في `Common/`.

التفاصيل والكود: [references/architecture.md](references/architecture.md)

## 3) المبادئ (System Principles) اللي ماشيين عليها

1. **Thin controllers, fat services.** الـ business logic كله في السيرفس.
2. **السيرفس بترجع `Result` / `Result<T>`** (`IsSuccess`, `Message`, `StatusCode`, `Errors`, `Data`)، وماترميش exceptions علشان أخطاء البيزنس. الكنترولر يحولها لـ HTTP بـ helper واحد (`From(result)`).
3. **Transaction واحدة لكل write request** (`TransactionActionFilter`): أي exception أو status ≥ 400 يبقى rollback.
4. **أي عملية فلوس أو حجز تبقى idempotent** (`[Idempotent]` + هيدر `Idempotency-Key`).
5. **الحماية من الـ race conditions على 3 طبقات:** lock في الـ app (`sp_getapplock`)، وre-check بعد الـ lock، و**unique / filtered unique index** كخط دفاع أخير. وعلى الصفوف الحساسة `rowversion` (optimistic concurrency).
6. **الـ multi-tenancy في الـ DbContext مش في كل كويري:** global query filter، وكمان guard في `SaveChangesAsync` بيمنع الكتابة في tenant تاني.
7. **أخطاء الداتابيز تتحول لرسائل مفهومة** في `GlobalExceptionMiddleware`: concurrency/deadlock/duplicate key بيرجعوا 409، والـ cross-tenant بيرجع 403، وغير كده 500 من غير stack trace.
8. **الإشعارات والـ side effects تطلع بعد الـ commit بس** (outbox في الذاكرة يتعمله `Flush` بعد الـ commit أو `Discard` بعد الـ rollback).
9. **حالات الـ entity بتتحكم فيها state machine** (dictionary فيه الانتقالات المسموحة)، مش `if` متفرقة.
10. **الـ pure rules منفصلة** (بدون EF وبدون HTTP) في `Rules.cs` أو static classes، وعليها unit tests.
11. **الكويريز:** `AsNoTracking` في القراءة، و`Select` للـ DTO بدل `Include` تقيل، والـ pagination في الداتابيز، و`ExecuteUpdateAsync` / `ExecuteDeleteAsync` للتعديلات الجماعية، وindex لكل فلتر أو ترتيب بيتكرر.
12. **DI lifetimes:** Scoped افتراضياً، وSingleton بس للحاجات الـ stateless أو الـ thread-safe. والـ singleton عمره ما ياخد scoped service مباشرة (يستخدم `IServiceScopeFactory`).
13. **الأسرار مش في الكود:** `IOptions<T>` من config، والمفاتيح المتخزنة في الداتابيز بتتشفر (`ISecretProtector`).
14. **الوقت:** خزّن `DateTime.UtcNow`، وحوّل للتوقيت المحلي من helper واحد (`TimeHelper.NowInEgypt` / `Clock.Today`).
15. **رسائل الخطأ للمستخدم بالعربي (أو بلغة العميل)، واللوجز بالإنجليزي** بـ structured logging (Serilog).

## 4) فهرس الباترنز: إمتى أستخدم إيه

| المشكلة | الباترن | المرجع |
|---|---|---|
| فصل الـ data access وحفظ كذا تغيير مرة واحدة | Generic Repository + Unit of Work | [data-access.md](references/data-access.md) §1-2 |
| نفس النتيجة من كل سيرفس | Result Pattern | [architecture.md](references/architecture.md) §3 |
| commit/rollback أوتوماتيك | Transaction Action Filter (Decorator حوالين الـ action) | [concurrency-idempotency.md](references/concurrency-idempotency.md) §1 |
| الـ client بيعمل retry أو المستخدم بيدوس مرتين | Idempotency Key | [concurrency-idempotency.md](references/concurrency-idempotency.md) §2 |
| اتنين بيحجزوا نفس الميعاد أو بيبيعوا آخر علبة | App lock + re-check + unique index + rowversion | [concurrency-idempotency.md](references/concurrency-idempotency.md) §3-5 |
| كل كويري تتفلتر بالـ tenant لوحدها | Global Query Filter + ICurrentUser | [multi-tenancy.md](references/multi-tenancy.md) |
| داتا كبيرة وكويريز بطيئة | Indexing + Projection + DB Pagination | [data-access.md](references/data-access.md) §3-5 |
| حاجة واحدة مشتركة لكل التطبيق (cache، presence، queue) | Singleton (DI) | [design-patterns.md](references/design-patterns.md) §1 |
| اختيار implementation وقت التشغيل | Service Provider / DI + Factory + Strategy | [design-patterns.md](references/design-patterns.md) §2-3 |
| بناء object أو policy أو كويري معقد خطوة بخطوة | Builder | [design-patterns.md](references/design-patterns.md) §4 |
| حالات order أو حجز | State Machine | [design-patterns.md](references/design-patterns.md) §5 |
| شغل تقيل ماينفعش يأخر الـ response | Producer/Consumer (Channel + BackgroundService) / Hangfire | [design-patterns.md](references/design-patterns.md) §6 |
| صلاحيات بتتغير من الداتابيز | Dynamic Policy Provider | [design-patterns.md](references/design-patterns.md) §7 |

## 5) Checklist: مشروع جديد من الصفر

1. اعمل الـ solution بالـ 3 projects (أو مشروع واحد بـ Modules لو صغير)، والـ references تبقى `Api → Core`، `Api → EF`، `EF → Core`.
2. في Core: `BaseEntity` (Id, CreatedAt, UpdatedAt)، و`Result`/`Result<T>`، و`IBaseRepository<T>`، و`IUnitOfWork`، و`ICurrentUser`، و`PagedResult<T>` + `PaginationDTO`.
3. في EF: `ApplicationDbContext` (conventions: enum كـ string، precision للـ decimal، tenant filters، `SaveChangesAsync` override)، و`BaseRepository<T>`، و`UnitOfWork`.
4. في Api: `ApiController` base فيه `From(Result)`، و`TransactionActionFilter` متسجل global، و`GlobalExceptionMiddleware`، و`IdempotencyFilter` + `[Idempotent]`، و`AddApplicationServices()` / `AddRepositories()` extension methods.
5. Auth: Identity + JWT (access + refresh). الـ claims فيها الـ tenant id. والصلاحيات يا إما roles يا إما `[AuthorizeAbility]` الديناميكية.
6. لكل entity جديدة: Model + `I<X>Repository : IBaseRepository<X>` + `<X>Repository : BaseRepository<X>` + property في الـ UoW + indexes في `OnModelCreating` لكل فلتر بيتكرر.
7. لكل feature: DTOs + `I<X>Service` + `<X>Service` (يرجع `Result<T>`) + Controller رفيع + تسجيل في `AddApplicationServices()`.
8. أي endpoint بيحرك فلوس أو بيحجز: `[Idempotent]` + lock + unique index.
9. Startup: Migrate + Seed (roles, admin) لو `Database:MigrateOnStartup` شغال.
10. Swagger + Serilog + CORS من config + Rate limiting + Response compression.

## 6) طريقة الشغل لما الـ skill دي تتفعّل

- قبل ما تكتب كود في مشروع موجود، **اقرا الـ Program.cs والـ DbContext والـ BaseRepository/UnitOfWork بتوعه** وامشي على الـ conventions اللي فيه، حتى لو مختلفة شوية عن هنا.
- في مشروع جديد امشي على الـ checklist، وانسخ الكود من الـ references.
- لو الـ feature موجودة في مشروع قديم (دفع، PDF، OTP…)، افتح `SERVICES_CATALOG.md` واقرا الملف الأصلي وانقل النمط بدل ما تكتب من الصفر.
- لو هتخرج عن باترن من دول، قول السبب للمستخدم.
