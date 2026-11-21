# كتالوج السيرفسيس — مرجع لكل المشاريع

الملف ده بيقسّم كل اللي اتعمل في المشاريع الستة لسيرفسيس (قدرات) جاهزة نرجعلها لما نبدأ مشروع جديد.
بدل ما نكتب من الصفر: نقول "خد سيرفس الـ X من مشروع Y" ونعدّل عليه.

> **طريقة الاستخدام مع Claude:** في أي سيشن جديدة قول مثلًا:
> "افتح `SERVICES_CATALOG.md` وخد سيرفس **الدفع بـ Stripe** من NED-Order وطبّقه في المشروع ده".
> كل سطر هنا فيه المسار الفعلي للملفات، فـ Claude يقدر يقرأ الكود الأصلي وينسخ النمط.

آخر تحديث: 2026-11-09

---

## 0) المشاريع ومساراتها (النسخة المعتمدة)

في نسخ مكررة على الجهاز — دي النسخ الأحدث اللي الكتالوج مبني عليها:

| الاسم المختصر | المشروع | المسار المعتمد | الستاك |
|---|---|---|---|
| **NED-API** | NED Order (باك) | `C:\Users\Mostafa\source\repos\NED-Order` | ASP.NET Core 8 API, EF Core, Identity, SignalR, Stripe, Datatrans, Revolut, Firebase, QuestPDF |
| **NED-WEB** | NED Order (فرونت) | `C:\Users\Mostafa\source\repos\NED-Order-main` | pnpm monorepo: `apps/web` (Next.js 16) + `apps/mobile` (Expo 57) + `packages/api-types` |
| **NEDVIEW** | NedView | `C:\Users\Mostafa\Desktop\projects\NedView\NedView` | ASP.NET Core MVC (مشروع واحد), SignalR, DigitalOcean Spaces (S3), PWA Service Worker |
| **KMG** | KMG | `C:\Users\Mostafa\Desktop\projects\KMG` | ASP.NET Core 8 API (3 layers) + `kmg-web` (Next.js 16) + Gemini AI + xUnit |
| **ELW** | الوسيط Elwasyet | `C:\Users\Mostafa\Desktop\projects\Elwasyet` | ASP.NET Core 8 API (3 layers), Hangfire, SignalR, Twilio, SMS Misr, QuestPDF, ClosedXML/EPPlus |
| **NEDX-API** | Nedx (باك) | `C:\Users\Mostafa\Desktop\projects\CRMProject` | ASP.NET Core 8 API, Hangfire, Stripe Connect, Google Calendar, QuestPDF |
| **NEDX-WEB** | Nedx (فرونت) | `C:\Users\Mostafa\Desktop\projects\CRMProject\Ned-x-master` | Next.js 15, next-intl, next-auth, React Query, Zustand, Axios |
| **SWISS** | NedSwiss | `C:\Users\Mostafa\Desktop\projects\NedSwiss\NedSwiss` | ASP.NET Core MVC (مشروع واحد), Session auth, OpenAI + Gemini, ClosedXML |

نسخ قديمة/مكررة **ماتعتمدش عليها**: `kmg-backend-split`, `kmg-frontend-split` (اتدمجوا في KMG)،
`source\repos\NEDf\Ned-x` (أقدم من Ned-x-master)، `NedView - Copy`.

---

## 1) هيكل المشروع (Skeleton) — تبدأ منين

| النمط | أحسن مرجع | امتى تستخدمه |
|---|---|---|
| **API بـ 3 Layers + Generic Repository + UnitOfWork + صلاحيات Ability ديناميكية** | **KMG** (الأحدث والأنضف) — الأصل كان ELW | أي نظام إدارة داخلي (ERP/محاسبة/موظفين). `KMG.Core/Interfaces/IUnitOfWork.cs`، `KMG.EF/Repositories/`، `KMG.Api/Program.cs` |
| **API بـ 3 Layers + Repos مسجلة بـ Extension Methods + `Result<T>` + Transaction تلقائي لكل Request** | **NED-API** | SaaS متعدد العملاء. `NED Order/Helper/ServiceExtenstions.cs` و`RepositoryExtensions.cs` (`AddRepositories()` / `AddApplicationServices()`)، `NEDOrder.Core/Helper/Result.cs`، `NEDOrder.EF/Filter/TransactionActionFilter.cs` (بيفتح Transaction لأي POST/PUT/DELETE ويعمل Rollback لو فيه Exception أو Result فاشل) |
| **MVC مشروع واحد (Controllers/Core/EF/Views)** | **SWISS** (Session) أو **NEDVIEW** (JWT في Cookie) | لوحة تحكم بسيطة بدون فرونت منفصل |
| **SaaS متعدد الشركات + Super Admin + اشتراكات** | **NEDX-API** | `SuperAdminService` (شركة + مدير في خطوة واحدة)، `SubscriptionTypeService`، `SubscriptionReminderJob` |
| **Monorepo فرونت (ويب + موبايل + Types مشتركة)** | **NED-WEB** | `pnpm-workspace.yaml`، `packages/api-types` |

ملاحظات مشتركة:
- **Global Exception Middleware:** `NED-API → NED Order/Helper/GlobalExCeptionMiddleWare.cs`
- **رد 403 موحّد:** `ELW → Elwasyet.Api/Helper/ForbiddenResponseMiddleware.cs`
- **AutoMapper Profile:** `ELW → Elwasyet.Api/Helper/MappingProfile.cs`، `NEDX-API → CRMProject.API/Helpers/MappingProfile.cs`
- **TimeSpan JSON Converter:** `NED-API → NEDOrder.Core/Helpers/TimeSpanConverter.cs`
- **توقيت محلي:** `KMG.Core/Helper/TimeHelper.cs`، `ELW → Elwasyet.Core/Helper/TimeHelper.cs`

---

## 2) المصادقة والصلاحيات (Auth & Permissions)

| السيرفس | المشروع | الملفات | بيعمل إيه |
|---|---|---|---|
| JWT + ASP.NET Identity (تسجيل/دخول) | NED-API | `NEDOrder.Core/Services/AuthService.cs`، `Helpers/JWT.cs` | تسجيل مطعم (Tenant) + دخول |
| JWT + Roles + Abilities | **KMG** | `KMG.Core/Services/AuthService.cs` | دخول، تسجيل موظف، تعديل، جلب الأدوار وصلاحيات كل دور |
| Auth كامل للموبايل (OTP) | **ELW** | `Elwasyet.Core/Services/AuthService.cs` | موظف + عميل + تطبيق، OTP بالـ SMS أو الإيميل، Reset Password |
| **صلاحيات ديناميكية (Ability) بـ Policy Provider** | **KMG** | `KMG.Api/Authorization/AbilityPolicyProvider.cs`، `AbilityAuthorizationHandler.cs`، `AuthorizeAbilityAttribute.cs`، `KMG.Core/Authorization/AbilityRequirement.cs` | `[AuthorizeAbility("x")]` على أي Endpoint، الصلاحيات في الداتابيز (Role ↔ Ability ↔ User) |
| نفس الفكرة (النسخة الأولى) | ELW | `Elwasyet.Api/Authorization/` | |
| Permission Handler | NEDX-API | `CRMProject.API/Helpers/PermissionHandler.cs` | |
| **API Key لجهات خارجية** | KMG | `KMG.Api/Authorization/ApiKeyAuthFilter.cs` | حماية Endpoints بتتنادى من خدمة خارجية (زي AI) مش من مستخدم |
| JWT في Cookie لـ MVC | NEDVIEW | `Core/Services/JwtService.cs`، `Helpers/JwtCookieMiddleware.cs` | مسارات عامة + تحقق من دور Admin + Redirect لو التوكن انتهى |
| Session Auth لـ MVC | SWISS | `Core/Helpers/SessionAuth.cs` | `[AllowGuest]`، `[AdminOnly]` |
| Claims Extensions | NED-API | `NEDOrder.Core/Helper/ClaimsExtensions.cs` | جلب UserId/RestaurantId من التوكن |
| SignalR UserId Provider | ELW | `Elwasyet.Core/Helpers/CustomUserIdProvider.cs` | ربط اتصال SignalR بالمستخدم |
| Google Login | NEDX-WEB/API | `next-auth` في الفرونت + `Google.Apis.Auth` | |
| Seed حساب الأدمن والأدوار | KMG / NED-API | `KMG.Api/Helper/DbInitializer.cs`، `NEDOrder.Core/Helpers/AdminSeeder.cs`، `DbInitializer.cs` | Migrate + Seed تلقائي أول تشغيل |

---

## 3) الدفع الإلكتروني (Payments)

| السيرفس | المشروع | الملفات | بيعمل إيه |
|---|---|---|---|
| **Stripe Checkout + Connect + Webhooks + Refund** | **NED-API** | `NEDOrder.Core/Services/StripePaymentService.cs`، `Controllers/StripeController.cs`، `Helper/StripeSettings.cs` | Checkout للطلب، حساب Connected لكل مطعم، حالة الحساب، Webhook بالتوقيع |
| **Datatrans (سويسرا)** | **NED-API** | `DatatransPaymentService.cs`، `Controllers/PaymentsController.cs`، `AdminPaymentsController.cs` | Checkout، Webhook، Refund، Cancel، إعدادات المنصة وإعدادات كل مطعم |
| **Revolut** | NED-API | `RevolutPaymentService.cs`، `Controllers/RevolutController.cs` | Checkout، Webhook، Refund |
| روابط دفع بتوكن | NED-API | `PaymentLinkService.cs` | إنشاء لينك والتحقق منه |
| **تشفير مفاتيح الدفع المخزنة** | NED-API | `SecretProtector.cs` | Protect/Unprotect لأي Secret في الداتابيز |
| سياسة الدفع | NED-API | `NEDOrder.Core/Helper/PaymentPolicy.cs` | مين يقدر يدفع بإيه |
| تسويات للتجار + PDF | NED-API | `SettlementService.cs` | تقرير تسوية، تعليم "تم التحويل"، PDF |
| Stripe OAuth Connect لكل شركة | NEDX-API | `StripeCompanyService.cs`، `CompanySettingsService.cs` (حفظ مفاتيح Stripe) | |
| Payment Link بـ Stripe | NEDX-API | `PaymentGatewayService.cs` | |
| صفحات نجاح/فشل الدفع | NEDX-WEB | `app/[locale]/payment-success`، `payment-failed` | |

---

## 4) PDF والمستندات و QR

| السيرفس | المشروع | الملفات |
|---|---|---|
| **إيصال طلب (QuestPDF)** | NED-API | `ReceiptPdfService.cs`، `ReceiptService.cs` |
| **عرض سعر بتصميمين (حديث/بسيط)** | NEDX-API | `QuestPdfGeneratorService.cs`، `ModernOfferDocument.cs`، `SimpleOfferDocument.cs` |
| فاتورة + VAT | NEDX-API | `InvoiceService.cs`، `CRMProject.CORE/Helpers/OfferVatCalculator.cs` |
| **فاتورة PDF + إرسالها واتساب** | ELW | `Elwasyet.Core/Services/InvoiceService.cs` (Twilio) |
| فواتير الكترونية من الفرونت | KMG | `IInvoiceService` + `kmg-web/src/app/(app)/invoices` |
| عروض أسعار وفواتير بلينك عام (Token) | SWISS | `ISalesService` (`GetQuoteByTokenAsync`، `GetInvoiceByTokenAsync`)، `Controllers/PublicController.cs` |
| QR لمنيو المطعم | NED-API | `QRCodeService.cs` (QRCoder) |

---

## 5) الإيميل و SMS و واتساب

| السيرفس | المشروع | الملفات | ملاحظات |
|---|---|---|---|
| **إيميلات بقوالب متعددة اللغات** | **NED-API** | `MailingService.cs`، `Helpers/EmailLocalization.cs`، `MailSettings.cs` | MailKit، ترحيب + تأكيد طلب + غيره بلغة العميل |
| إيميلات تشغيلية كتير (عروض/مهام/مواعيد) | NEDX-API | `SmtpEmailService.cs`، `Exceptions/InvalidRecipientEmailException.cs` | بيتعامل مع الإيميل الغلط وبيبعت تنبيه |
| إيميل + كود تحقق | ELW / SWISS | `MailingService.cs` (`GenerateCode`) | |
| **SMS OTP (SMS Misr)** | ELW | `SmsMisrService.cs`، `Const/SmsMisrSettings.cs` | إرسال + تحقق |
| **WhatsApp (Twilio)** | ELW | `Const/TwilioSettings.cs`، `InvoiceService.cs`، `RequestNotifcationService.cs` | |

---

## 6) الإشعارات والـ Realtime

| السيرفس | المشروع | الملفات | بيعمل إيه |
|---|---|---|---|
| **Push Notifications (Firebase FCM)** | **NED-API** | `NotificationService.cs` (+ `FirebaseAdmin` في `Program.cs`) | تسجيل توكن الجهاز، إشعار طلب جديد/مدفوع/حجز، مقروء/غير مقروء |
| Push + أصوات وقنوات على الموبايل | NED-WEB | `apps/mobile/src/lib/notifications/` (push, channels, sounds, sound-preference) | |
| **SignalR Hub + تواجد (Online/Heartbeat)** | NED-API | `Hubs/RestaurantHub.cs`، `PresenceService.cs` | معرفة المطعم أونلاين ولا لأ |
| عميل SignalR على الموبايل | NED-WEB | `apps/mobile/src/lib/realtime/signalr.ts`، `realtime-provider.tsx` | |
| **إشعارات داخل النظام بـ SignalR** | ELW | `Hubs/NotificationHub.cs`، `NotificationService.cs` (`SendToUserConnectionsAsync`) | |
| إشعارات مجدولة (انتهاء طلبات، مخزون قليل) | ELW | `OrderNotificationService.cs`، `CashBoxNotificationService.cs` + Hangfire | |
| إشعارات بسيطة في الداتابيز | KMG / NEDX-API | `NotificationService.cs` | Active/Read/Dismiss |
| **Hub لأجهزة (Screens) مع إعادة اتصال ذكية** | NEDVIEW | `Core/Hubs/ScreenHub.cs` | عدّ الاتصالات لكل جهاز، إرسال محتوى بإصدار (Version) |

---

## 7) الـ Background Jobs

| النوع | المشروع | الملفات |
|---|---|---|
| **Hangfire (SQL Server) + Dashboard** | ELW | `Elwasyet.Api/Program.cs` (AddHangfire) + سيرفسيس الإشعارات |
| Hangfire Job تذكير الاشتراكات | NEDX-API | `CRMProject.CORE/Helpers/Hangfire/SubscriptionReminderJob.cs` |
| `BackgroundService` تنظيف دوري | NEDVIEW | `Core/Services/ScreenCleanupService.cs` (كل ساعة) |
| `BackgroundService` إعداد عند التشغيل | NEDVIEW | `Core/Services/StorageSetupService.cs` (ضبط CORS على الـ CDN) |
| Worker لتحديث حالة الفواتير | SWISS | `EF/Services/InvoiceStatusWorker.cs` |

---

## 8) رفع وتخزين الملفات

| السيرفس | المشروع | الملفات |
|---|---|---|
| **تخزين قابل للتبديل (Local ↔ S3/DigitalOcean)** خلف Interface واحد | **NEDVIEW** | `Core/Interfaces/IVideoStorageService.cs`، `DigitalOceanSpacesService.cs`، `LocalVideoStorageService.cs`، `Helpers/DigitalOceanSettings.cs` |
| FileService محلي (Upload/Save/Delete) | NED-API / NEDX-API / SWISS | `FileService.cs` |
| مستندات وصور ومرفقات الطلب | ELW | `FileService.cs`، `DocumentsService.cs`، `AppServices` (`IImageService`) |
| مرفقات المشروع + Endpoint ملفات | KMG | `ProjectService` (Attachments)، `Controllers/FilesController.cs` |
| ملفات المهام (متطلبات/نتايج) | SWISS | `TaskFileService.cs` |
| صور المطعم (لوجو/غلاف/جاليري) | NED-API | `RestaurantService.cs` |

---

## 9) Excel (تصدير واستيراد)

| السيرفس | المشروع | الملفات |
|---|---|---|
| **تصدير تقارير كتير لـ Excel (باك)** | **ELW** | `ExcelExportReportService.cs` (يومي، تنفيذي، فروع، موظفين، طلبات، مصاريف، عمولات...) |
| **استيراد عملاء من Excel** | ELW | `ExcelClientImportService .cs` |
| أرشيف/تصدير بيانات | SWISS | `EF/Services/ArchiveService.cs` (ClosedXML) |
| **تصدير Excel من الفرونت (بتنسيق)** | KMG | `kmg-web/src/lib/excel.ts`، `excelStyled.ts`، `projectExport.ts` |

---

## 10) الذكاء الاصطناعي (AI)

| السيرفس | المشروع | الملفات | بيعمل إيه |
|---|---|---|---|
| **AI Chat بـ Gemini + Tool Calling على بيانات النظام** | **KMG** (الأنضج + عليه تيستات) | `KMG.Api/Ai/` (`GeminiChatClient.cs`، `AiChatTools.*.cs` متقسمة لكل دومين: Finance/Projects/Stock/People/Parties)، `AiChatService`، `KMG.Tests/AiChat*.cs` | المستخدم يسأل والـ AI ينادي Tools تقرأ من الداتابيز |
| AI Context + تجميع مناقصات (Tender Scouting) | KMG | `AiContextService` + `ApiKeyAuthFilter` | خدمة خارجية تبعت نتايج مناقصات للنظام |
| مساعد AI بـ Tools (OpenAI أو Gemini) | SWISS | `EF/Services/Ai/AssistantService.cs`، `AssistantTools.cs`، `GeminiClient.cs`، `OpenAiChatClient.cs`، `CompanyDataService` | نفس الفكرة بمزوّدين قابلين للتبديل |
| Insights وتنبيهات وبحث | SWISS | `InsightsService.cs` | |

---

## 11) لوحات التحكم والتقارير

| المشروع | الملفات |
|---|---|
| NED-API | `DashboardService.cs` (Charts أسبوع/شهر)، `AdminDashboardService.cs` (للمنصة) |
| ELW | `CompanyDashboardService.cs`، `OrderService` (تقرير يومي/متقدم/تنفيذي، إحصائيات فروع وموظفين)، `ActivityReportService.cs` |
| NEDX-API | `CompanyDashboardService.cs`، `DashboardService.cs` (Super Admin)، `ExpenseService` (Charts شهري وحسب الفئة) |
| KMG | `DashboardService.cs` |
| SWISS | `DashboardService.cs`، `FinanceService` (Profit & Loss)، `SalesService.GetOverviewAsync` |

---

## 12) موديولات البيزنس (Domain) — جاهزة للنسخ

### مطاعم وطلبات أونلاين — NED-API + NED-WEB
- **مطعم/فروع/ساعات عمل:** `RestaurantService`، `BranchService`
- **منيو متعدد + أقسام + منتجات + Add-ons + تعديل خاص بكل فرع:** `MenuService`، `AddOnGroupService`
- **طرق الخدمة (توصيل/استلام/صالة/حجز) + مناطق توصيل:** `ServicesSettingsService` (`ResolveZoneAsync`)
- **حساب سعر الطلب (ضرايب، توصيل، خصومات):** `OrderPricingService.cs` (1000+ سطر)
- **الطلبات وتتبعها:** `OrderService` (`TrackOrderAsync`، `CreateCustomerOrderAsync`)
- **محرك عروض/بروموكود بـ Validators:** `PromoService`، `NEDOrder.Core/Helper/Promo/Validators/`، `Helper/PromoTemplateSeeder.cs`
- **حجوزات:** `ReservationService`
- **عملاء + عناوين + تاريخ طلبات:** `ICustomerService` — ⚠️ الكلاس `CustomerService` متحطوط غلط جوه `NEDOrder.Core/DTOs/Services/UpdateDeliverySettingsDTO.cs` (انقله لملف لوحده لو هتنسخه)
- **مصاريف:** `ExpenseService` (ملخص شهري/سنوي)
- **Storefront/محرر الموقع/الهوية:** `BrandSettingsService`، `StorefrontController`، `Helpers/StorefrontConfigValidator.cs`، فرونت `apps/web/src/lib/storefront/`
- **طباعة على طابعة (موبايل):** `apps/mobile/src/lib/printing/`
- **مسودة طلب أوفلاين:** `apps/mobile/src/lib/order-draft/`

### إدارة أعمال داخلية / محاسبة — KMG
- **مشاريع بـ 3 أنواع + دفعات + مصاريف + مرفقات + شطب + شيكات معلقة + Audit Log:** `ProjectService`
- **مخزون:** `StockService` (فئات، دمج خامات، Price Batches، شراء/صرف/مرتجع/تسوية، تنبيه نقص)
- **خزينة:** `CashBoxService` (مصاريف نثرية، تعديل رصيد، عكس حركة)
- **رواتب + سلف + خصومات وحوافز + معاينة قبل الصرف:** `PayrollService`
- **تأمينات (عادي + مصر):** `InsuranceService`، `MisrInsuranceService`
- **عُهد ومأموريات:** `CustodyService`، `MissionService`
- **عملاء + موردين بدفعات وشيكات:** `ClientService`، `SupplierService`
- **موظفين بدون حساب دخول (عمال):** `EmployeeService.CreateWorkerAsync`
- **بيانات تجريبية للفرونت:** `KMG.Api/Helper/DummyDataSeeder.cs`

### خدمات/طلبات بفروع ومناديب — ELW
- **دورة حياة الطلب (State Machine) + Audit:** `OperationLogService.cs`، `Const/OrderChangeTracker.cs`، `OrderStatusEnum.cs`، `Helper/OrderStatusSeeder.cs`
- **خزينة لكل موظف + تحويلات:** `CashierService.cs`
- **عمولات بالأدوار + مناديب:** `CommissionService.cs`، `AgentService`، `AgentTransactionService.cs`
- **مخزون بالفروع + طلبات تحويل بموافقة:** `StockService.cs`، `RequestStockService.cs`
- **خدمات بمستندات مطلوبة وWorkflow:** `ServiceService.cs`
- **عروض + مصاريف إضافية على الطلب:** `OfferService.cs`، `OrderOverheadsService.cs`
- **تطبيق العميل:** محفظة مستندات، نقاط، Cash Back، مدونة، تصنيفات → `Elwasyet.Core/AppServices/`
- **Validation شرطي:** `CustomValidation/RequiredIfOfferAttribute.cs`

### CRM لشركات خدمات (SaaS) — NEDX-API + NEDX-WEB
- **عملاء، موظفين وأداءهم، مهام (بدء/إنهاء + صور)، مواعيد:** `CustomerService`، `EmployeeService`، `TaskService`، `AppointmentService`
- **عروض أسعار بموافقة/رفض من العميل بلينك + إيميل:** `OfferService` (`ApproveOfferAndNotifyAsync`) + صفحات `app/[locale]/offer/accepted|rejected`
- **مزامنة Google Calendar:** `GoogleCalendarService.cs`، `GoogleCalendarController.cs`
- **ماركت بليس إعلانات خدمات:** `ServiceAdService`، `MarketplaceController`
- **فواتير مع Autocomplete لرقم العرض:** `InvoiceService`

### CRM/مبيعات لشركة واحدة — SWISS
- **منتجات، Pipeline صفقات (Kanban)، ملاحظات، عروض أسعار، فواتير، Client 360:** `SalesService` (ملف واحد شامل)
- **مشاريع + مراجعات وتعليقات:** `ProjectService`
- **مالية (إيرادات/مصاريف/P&L):** `FinanceService`

### شاشات عرض رقمية (Digital Signage) — NEDVIEW
- **تسجيل شاشة بكود، ربطها بمستخدم، Playlist، Push محتوى، أوامر عن بعد، إحصائيات تشغيل:** `ScreenService.cs`
- **مشغّل يشتغل أوفلاين بعد انقطاع الكهرباء:** `wwwroot/sw.js` (Service Worker بيكاش الصفحة والفيديوهات)، `wwwroot/js/cache-manager.js`، `screen-player.js`

---

## 13) الفرونت اند — مراجع جاهزة

| المرجع | المشروع | فين |
|---|---|---|
| **Next.js 16 + RHF + Zod + Tailwind 4 + Route Groups** (admin/auth/dashboard/marketing/store) | NED-WEB | `apps/web/src/app/` |
| **i18n بقواميس (ar/en/de/nl) بدون مكتبة** | NED-WEB | `apps/web/src/lib/i18n/` (+ `TRANSLATION_GUIDE.md`) |
| **SEO (Metadata + Structured Data)** | NED-WEB | `apps/web/src/lib/seo/` |
| API Client لكل موديول | NED-WEB / KMG | `apps/web/src/lib/api/*.ts`، `kmg-web/src/lib/api-client.ts` |
| صفحات مساعدة (Help) لكل صفحة | NED-WEB / KMG | `apps/web/src/lib/help/`، `kmg-web/src/lib/help-content.ts` |
| **تطبيق موبايل Expo** (تابات، Push، SignalR، طباعة) | NED-WEB | `apps/mobile/` |
| **Next.js 15 + next-intl `[locale]` + next-auth + React Query + Zustand + Axios** | NEDX-WEB | `app/[locale]/`، `lib/`، `hooks/`، `i18n/` (ar/de/en/it) |
| لوحة إدارة داخلية + Excel + جداول | KMG | `kmg-web/src/` (`lib/useTableState.ts`، `lib/excel*.ts`) |
| فلترة/بحث بـ Debounce | NEDX-WEB | `hooks/useDebounce.ts` |

---

## 14) متفرقات مفيدة

| الحاجة | المشروع | الملف |
|---|---|---|
| تسميات Enums بالعربي | KMG | `KMG.Core/Helper/EnumLabels.cs` |
| عملات | SWISS | `Core/Helpers/Currencies.cs` |
| حساب VAT | NEDX-API / NED-WEB | `OfferVatCalculator.cs`، `apps/web/src/lib/pricing/tax.ts` |
| حساب عمولة المنصة | NED-WEB | `apps/web/src/lib/commission.ts` |
| خرائط وعناوين | NED-WEB | `apps/web/src/lib/maps.ts`، `address.ts` |
| تيستات xUnit | KMG | `KMG.Tests/` |
| ملف تعليمات للـ AI جوه المشروع | NED-WEB / KMG | `CLAUDE.md`، `AGENTS.md` |

---

## اقتراح سريع حسب نوع المشروع الجديد

| لو المشروع الجديد... | ابدأ من |
|---|---|
| نظام إدارة/محاسبة لشركة | هيكل **KMG** كامل (باك + kmg-web) |
| SaaS فيه تجار ودفع أونلاين | هيكل **NED-API** + سيرفسيس الدفع + NED-WEB |
| CRM متعدد الشركات | **NEDX** (باك + فرونت) |
| تطبيق عملاء بموبايل وOTP ونقاط | **ELW** (Auth + AppServices) + `apps/mobile` من NED-WEB |
| لوحة MVC سريعة بدون فرونت منفصل | **SWISS** أو **NEDVIEW** |
| أجهزة/IoT بتتصل Realtime | **NEDVIEW** (ScreenHub + Service Worker) |
