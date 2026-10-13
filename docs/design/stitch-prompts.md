# ZOOD — Stitch Prompts (by package)

## How to use

1. Run each package as its own Stitch generation. Packages: POS, Receipts, Company mobile, Company desktop, Station, Operations.
2. **Always paste the GLOBAL BLOCK first, then the package prompt.**
3. Mobile packages: use Stitch **Mobile** mode. Desktop packages: use **Web** mode.
4. If Stitch drops RTL, add: "The entire layout is mirrored for RTL; navigation sits on the right."
5. If a result is too crowded, reply: "Remove 30% of the content. Keep only what answers: what is happening, what needs attention, what can I do next."
6. Variants. After a package looks right in Arabic + Light, generate variants of 2–3 key screens only (not every screen):
   - "Regenerate these screens in English, LTR, same layout mirrored, same density."
   - "Regenerate these screens in Dark theme using the dark tokens from the global block. Do not invert; keep numbers and tables highly readable."
   - For POS, generate Dark only for Home and Success, with high contrast. POS default stays Light (outdoor use).

---

## GLOBAL BLOCK (paste before every package)

```
PRODUCT
ZOOD (زُود) is a B2B fleet fuel management platform in Egypt. Companies prepay a corporate wallet. Each vehicle carries an NFC tag. Station attendants fuel vehicles using an Android handheld POS. ZOOD Operations runs the network. This is a financial + operations product: it must feel trustworthy, calm, fast and premium.

LANGUAGE & DIRECTION
The product is fully bilingual: Arabic (RTL) and English (LTR), both first-class. Generate the Arabic RTL version first unless asked otherwise.
Arabic: all copy in Arabic, RTL layout. Currency after the number: "760.00 ج.م". Dates like "12 أكتوبر • 11:42 ص".
English: same layout mirrored to LTR, copy in English. Currency: "EGP 760.00". Dates like "12 Oct • 11:42 AM". Liters: "40.00 L".
Western digits (0-9) in both languages. Egyptian plates always shown as registered: "أ ب ج 4582", even in English.
Directional icons (arrows, chevrons, back) mirror with the language. A language switch "العربية | English" lives in the user menu and on login screens.

THEME
Light (default) and Dark are both designed, not inverted.
Light: background #F7F5F0, surface #FFFFFF, text #1B1F1D, primary #0F3B2C.
Dark: background very dark green-black #0C1411, surface #131D19, elevated surface #1A2621, borders #2A3832, text warm off-white #EEEAE2, muted gray-green #9AA59E, primary adapted green #5CC392 (dark text on it), accent orange #F5A04A. Financial numbers, tables and charts must stay highly readable.
Status in dark: success #4CC38A, warning #E8B04B, danger #F07A6E, info #6AA8E8.
Status is always icon + text + color (e.g., "✓ مكتملة" / "✓ Completed", "⚠ تحتاج مراجعة" / "⚠ Needs Review").

BRAND (from the ZOOD logo)
- Primary: deep petroleum green #0F3B2C (sidebar, primary buttons, key headings). Lighter tints for hover/selected: #1E5A43, #E8F0EB.
- Background: warm off-white #F7F5F0.
- Surfaces: white #FFFFFF; subtle warm neutral #F0ECE4; borders #E5E0D6.
- Text: #1B1F1D primary, #6B6F6A secondary.
- Accent: orange #F28C28, used sparingly (at most one accent element per screen, e.g. active nav indicator or one key highlight). Never use orange for warnings or errors.
- Status: success #1F8A5B, warning amber #B7791F, danger #C2362B, info #2F6FB0. Status is always a small dot or chip WITH text, never color alone.
- Logo: Arabic wordmark "زُود" with orange speed lines and Latin "ZOOD" below; tagline "كمل طريقك". Use small in headers. White version on green, green version on light backgrounds.

TYPOGRAPHY
IBM Plex Sans Arabic. Page title 24/32 semibold (mobile 20/28). Section title 16/24 semibold. Body 14/22. Numbers in KPIs 28-32 semibold with tabular figures.

STYLE
Flat surfaces with 1px borders. Shadows only on floating layers (bottom sheets, menus, dialogs). Radius: 12px containers, 10px buttons and inputs, full-round chips. Generous whitespace, 8px spacing grid. Simple line icons (Lucide/Phosphor style), mirrored for RTL where directional.
DO NOT USE: glassmorphism, neon, gradients (except inside the logo), illustrations, stock photos, emoji, 3D, card-inside-card, rainbow charts.

DENSITY RULES (very important)
Every screen answers three questions: What is happening? What needs attention? What can I do next?
- Max 4 KPIs per screen.
- One primary action per screen.
- Prefer clean list rows with dividers over many separate cards.
- Secondary information lives behind "عرض الكل" or a detail screen (progressive disclosure).
- Never show data just because it exists.

RESPONSIVE RULES
- Mobile 390px: bottom navigation with exactly 5 items (rightmost first in RTL), no sidebar.
- Tablet 768px: collapsed icon rail on the right + content.
- Desktop 1440px: grouped sidebar on the right (RTL), max content width ~1200px.
- Tables on desktop become compact list rows on mobile. No horizontal scrolling.
- Filters on mobile open in a bottom sheet. Long forms become steps.
- Touch targets at least 44px.

DEMO DATA (fictional, use consistently)
Company: النيل للنقل والخدمات — Fleet manager: خالد فؤاد
Wallet balance: 842,350.00 ج.م
Vehicles:
- أ ب ج 4582 — تويوتا كورولا 2024 — بنزين 92 — قسم التوزيع — فئة عادية
- س م ر 1274 — ميتسوبيشي فوسو 2022 — سولار — قسم النقل الثقيل — فئة عادية
- د هـ و 3906 — إيسوزو NPR 2021 — سولار — قسم النقل الثقيل — فئة عادية
- ل م ن 5521 — نيسان صني 2023 — بنزين 92 — قسم التوزيع — فئة عادية
- ك ر م 7710 — هيونداي توسان 2023 — بنزين 95 — الإدارة — فئة VIP
Stations: محطة القاهرة الجديدة، محطة مدينة نصر
Fuels: بنزين 92، بنزين 95، سولار
Station agent: محمود عادل (رقم العامل 10427)
Key transaction: TX-48213 — أ ب ج 4582 — محطة القاهرة الجديدة — بنزين 92 — 40 لتر — 760.00 ج.م — مكتملة
Budgets (this month): قسم النقل الثقيل 500,000 ج.م (used 312,400) — قسم التوزيع 180,000 ج.م (used 141,900) — الإدارة 60,000 ج.م (used 18,250)
Limit example: أ ب ج 4582 — 50 لتر / يوم
Cooling-off between fuelings: 3 ساعات
Never use Lorem Ipsum.
```

---

## PACKAGE 1 — Station POS (Android handheld)

**Stitch mode:** Mobile. **Frame:** Android handheld POS, portrait, ~360×720.

```
Design the ZOOD Station POS app for an Android handheld payment terminal (Sunmi-style, with built-in receipt printer). It is used by fuel station attendants all day, standing outdoors, often in sunlight, sometimes wearing gloves.

This is NOT a dashboard. It must feel like a payment terminal: extremely simple, very fast, big targets, high contrast, minimal text, one primary action per screen.

POS-specific style overrides:
- White background, text #1B1F1D, primary buttons deep green #0F3B2C with white text.
- Primary buttons full width, 64px tall, 20px text. Body text 18px. Key numbers 40-56px.
- A thin top status bar on every screen: station name (small), agent name (small), connectivity pill: "متصل" (green dot) or "غير متصل" (amber dot).
- No sidebar, no bottom navigation, no charts, no tables.
- Never show the wallet balance amount, tag IDs, device IDs, or risk scores to the attendant.

Screens:

1. Login — ZOOD logo small at top. Field "رقم العامل" (prefilled 10427). Below: six PIN dots and a large numeric keypad (3×4, 64px keys). Small text: "يعمل بدون إنترنت". No other elements.

2. Home — Top status bar. Center: very large circular or rounded-square primary button "مسح المركبة" with an NFC/tap icon. Below it, two small quiet indicators side by side: "معاملات اليوم 23" and "بانتظار المزامنة 0". Bottom: two small text links "سجل الوردية" and "إنهاء الوردية". Nothing else.

3. Scanning — Large animated tap/NFC icon, text "قرّب الجهاز من شريحة المركبة", secondary button "إلغاء".

4. Vehicle found — Large plate block "أ ب ج 4582" (styled like an Egyptian plate: rectangle with a thin top strip), below it "تويوتا كورولا 2024". One row of three chips: fuel "بنزين 92", tier "عادية", wallet status "الرصيد متاح" (green). Remaining daily limit line: "المتبقي اليوم: 50 لتر". Primary button "متابعة".

5. Fuel entry — Segmented toggle "لتر | مبلغ". Huge value display "40.00 لتر" and under it the auto-calculated "760.00 ج.م" (price 19.00 ج.م/لتر shown small). Large numeric keypad. Primary button "مراجعة".

6. Confirm (bottom sheet over fuel entry) — Title "تأكيد العملية". One clear sentence: "خصم 760.00 ج.م — 40 لتر — أ ب ج 4582". Primary button "تأكيد" with a short progress fill indicating a 1-second anti-accidental-tap delay. Secondary "رجوع".

7. Success — Large green check, "تمت العملية بنجاح", amount 760.00 ج.م, plate, "TX-48213", time. Small badge "تمت المزامنة". Two buttons: primary "طباعة الإيصال", secondary "إنهاء".

8. Success with print failure — Same as success, plus an amber inline banner: "تعذرت الطباعة — تم إرسال الإيصال الرقمي". Actions: "إعادة الطباعة", "عرض رمز QR". Transaction is still shown as successful.

9. Cooldown rejection — Amber clock icon, title "تم تزويد هذه المركبة مؤخرًا", large countdown "متاح بعد 2:47 ساعة", plate below. One button "رجوع للرئيسية".

10. Declined — Red icon, "تم رفض العملية", one-line reason "تم تجاوز الحد اليومي للمركبة (50 لتر)", plate. One button "رجوع للرئيسية". No technical details.

11. Offline home — Same as Home but connectivity pill "غير متصل" (amber) and a slim persistent banner at top: "وضع عدم الاتصال — ستتم مزامنة العمليات تلقائيًا". "بانتظار المزامنة 2" is highlighted.

12. Offline success — Like Success but badge "بانتظار المزامنة" (amber clock) instead of "تمت المزامنة".

13. Shift history — Simple list of today's transactions: time, plate, liters, amount, and a small sync icon (synced / pending). Tapping a row offers "إعادة الطباعة". Header shows count and total.

14. End shift summary — Totals split in two rows: "محتسبة: 21 عملية — 15,960.00 ج.م" and "غير محتسبة (VIP): 2 عملية — 85 لتر". Line "بانتظار المزامنة: 0". Primary button "إنهاء الوردية وطباعة التقرير".

15. VIP variant of Vehicle found — Same as screen 4 but tier chip "VIP" (dark green with small orange star), no wallet status, no remaining limit. Primary button "إدخال الكمية". (VIP skips the confirmation step.)
```

---

## PACKAGE 2 — Receipts (thermal + digital)

**Stitch mode:** Mobile.

```
Design two receipt artifacts for ZOOD.

A) THERMAL RECEIPT PREVIEW — shown on screen as a realistic 58mm paper strip (384px wide, monochrome black on white, no colors, no grey fills, no heavy black areas). Paper usage must be minimal: target 7–9 cm long. No separators unless absolutely necessary, no blank lines, small logo.
Content, in this order, compact lines:
- "ZOOD | زُود" small text logo (not a large image)
- "محطة القاهرة الجديدة"
- "12/10/2026  11:42      TX-48213"
- "أ ب ج 4582 · بنزين 92"
- "40.00 لتر"
- "الإجمالي  760.00 ج.م" (bold, slightly larger)
- "✓ عملية ناجحة" and a small QR code (≈15mm) on the same row
Do NOT include: wallet balance, tag ID, device ID, company details, limits, agent ID, sync info.
Also show a VIP variant where the total line reads "غير محتسبة على المحفظة" instead of an amount.

B) DIGITAL RECEIPT — mobile page opened from the QR code (390px). Progressive disclosure:
Top: ZOOD logo small, green check, status "مكتملة", large amount "760.00 ج.م".
Primary rows: المركبة أ ب ج 4582 · المحطة محطة القاهرة الجديدة · الوقود بنزين 92 · الكمية 40 لتر · التاريخ 12 أكتوبر • 11:42 ص.
Collapsed section "تفاصيل إضافية" (shown expanded in a second screen): سعر اللتر 19.00 ج.م · رقم المعاملة TX-48213 · العامل محمود عادل · جهاز نقطة البيع POS-NC-02 · مصدر العملية: متصل · وقت المزامنة.
Bottom: "تحميل PDF" secondary button. Nothing else.
```

---

## PACKAGE 3 — Company portal, MOBILE (390px)

**Stitch mode:** Mobile.

```
Design the ZOOD Company portal for a fleet manager on a phone (390px). Company: النيل للنقل والخدمات. User: خالد فؤاد.
Bottom navigation, exactly 5 items, RTL order: الرئيسية · المعاملات · المركبات · التنبيهات · المزيد. Active item: green icon + label with a small orange indicator.
Top app bar: page title, and on detail pages a back arrow (pointing right in RTL).
Company users can VIEW fleet data. Changes (add vehicle, change limit, top up) are submitted as requests to ZOOD Operations, so use "طلب ..." actions, not edit forms.

Screens:

1. Login — logo, "تسجيل الدخول", email, password, primary button "دخول". Clean, nothing else.

2. Home — VERY light. Greeting "صباح الخير، خالد". A single balance block: "رصيد المحفظة" 842,350.00 ج.م with a small status "كافٍ لحوالي 18 يومًا". Two compact KPIs side by side: "إنفاق اليوم 12,480 ج.م" and "المركبات النشطة 46". Section "يحتاج انتباهك" with max 2 rows (e.g., "قسم التوزيع استهلك 79% من ميزانية الشهر", "س م ر 1274 وصلت للحد اليومي") and "عرض الكل". Simple bar/line spending chart for the last 7 days (single green color). "آخر المعاملات" with 3 rows and "عرض الكل". Stop there — no extra sections.

3. Transactions — Search field and a "تصفية" button (opens bottom sheet). Grouped by day ("اليوم", "أمس"). Each row: plate (bold) + station name below; amount on the opposite side with status chip below (مكتملة / مرفوضة / بانتظار المزامنة); small time. No tables.

4. Filter bottom sheet — Date range, vehicle, station, status, tier. Buttons "تطبيق" and "مسح".

5. Transaction details — Top: status chip, large amount 760.00 ج.م, plate and station. Primary rows: الوقود، الكمية، التاريخ والوقت. Collapsed "تفاصيل إضافية": سعر اللتر، رقم المعاملة، العامل، جهاز نقطة البيع، مصدر العملية (متصل/غير متصل)، وقت الجهاز، وقت المزامنة، قيد المحفظة. Secondary action "مشاركة الإيصال".

6. Vehicles — Search + filter. Rows: plate (styled small Egyptian plate), model, fuel chip; opposite side: "آخر تزويد منذ ساعتين" and a small status dot (نشطة / موقوفة). Tier chip only for VIP.

7. Vehicle details — Header: plate, model, tier, status. Three compact facts: "استهلاك الشهر 412 لتر", "إنفاق الشهر 7,840 ج.م", "آخر تزويد اليوم 11:42". Section "حد الاستهلاك": "50 لتر / يوم — المتبقي اليوم 10 لتر" with a thin progress bar. Section "الشريحة": status "نشطة" only (no full tag ID). "آخر المعاملات" 3 rows + "عرض الكل". Secondary action "طلب تعديل".

8. Wallet — Large balance. Small line "آخر شحن: 500,000.00 ج.م — 1 أكتوبر". Primary action "طلب شحن رصيد". Tabs: "الحركات" | "عمليات الشحن". Ledger rows: type icon (خصم وقود / شحن رصيد / عكس عملية), reference, amount (+ green / − neutral), resulting balance small.

9. Budgets — Explain at top in one short line: "الميزانية = سقف إنفاق شهري للقسم". List of departments: name, "312,400 من 500,000 ج.م", progress bar (amber over 75%, red over 100%), vehicles count.

10. Budget details — Department name, budget, spent, remaining, days left in month, small daily spending chart, top 3 vehicles by spend, action "طلب تعديل الميزانية".

11. Limits — Explain at top: "الحد = أقصى استهلاك مسموح للمركبة". List of limit rules: "50 لتر / يوم" — applies to 12 vehicles; "1,500 ج.م / أسبوع" — 8 vehicles; time window "من 6 ص إلى 10 م". Tap → vehicles under the rule.

12. Alerts — Rows with severity dot, title, vehicle/station, time. Examples: "رصيد المحفظة سيصل للحد الأدنى خلال 3 أيام", "س م ر 1274 وصلت للحد اليومي", "لوحة غير مؤكدة — د هـ و 3906". Tap → short detail sheet with one recommended action.

13. More — Simple grouped menu: الوقود (المحفظة، الميزانيات، الحدود) · الدعم (الطلبات) · التقارير · الحساب (الملف الشخصي، تسجيل الخروج). Plain rows with icons, no cards.

14. New request sheet — Bottom sheet "طلب جديد": request type (إضافة مركبة، تعديل حد، شحن رصيد، استبدال شريحة، أخرى), vehicle selector, note, "إرسال الطلب".
```

---

## PACKAGE 4 — Company portal, DESKTOP (1440px) + TABLET note

**Stitch mode:** Web.

```
Design the desktop (1440px) version of the ZOOD Company portal. Same content and density as the mobile version, using space for clarity — not for more data.

Layout: grouped sidebar on the RIGHT (RTL), deep green #0F3B2C background, white logo at top, groups with small grey group titles:
- الرئيسية
- الأسطول: المركبات
- الوقود: المعاملات · المحفظة · الميزانيات · الحدود
- التنبيهات
- التحليلات: التقارير
- الدعم: الطلبات
- الإعدادات
Active item: lighter green background + small orange indicator bar. Top bar: page title, global search, notifications bell, user avatar with name "خالد فؤاد — مدير الأسطول".

Screens:

1. Home — Row of exactly 4 KPIs (plain tiles, border only): رصيد المحفظة 842,350 ج.م · إنفاق اليوم 12,480 ج.م · معاملات اليوم 38 · المركبات النشطة 46 (عادية 44 / VIP 2). Below: two columns — wide column: spending chart "الإنفاق — آخر 30 يومًا" (single series, green); narrow column: "يحتاج انتباهك" with max 4 rows. Below full width: "آخر المعاملات" table with 5 rows and "عرض الكل". Nothing else.

2. Transactions — Filter bar (date range, vehicle, station, status, tier) and search. Clean table: رقم المعاملة · المركبة · المحطة · الوقود · الكمية · المبلغ · الحالة · الوقت. Offline-originated rows show a small icon with tooltip "تمت عبر وضع عدم الاتصال". Clicking a row opens a side panel (from the left in RTL) with the transaction details (same progressive disclosure as mobile).

3. Vehicles — Table: المركبة · الطراز · الوقود · سعة الخزان · القسم · الفئة · الحالة · إنفاق الشهر · آخر تزويد. Action button top: "طلب إضافة مركبة".

4. Vehicle details — Two columns: left narrow summary (plate, model, tier, status, tag status, department), right wide: limit status, consumption chart (monthly), recent transactions table.

5. Wallet — Balance block with "طلب شحن رصيد", then ledger table: التاريخ · النوع · المرجع · المبلغ · الرصيد بعد الحركة. Tab for top-up history.

6. Budgets — Department table with progress bars; click → budget detail page.

7. Limits — Rules table: القاعدة · النوع (لتر/مبلغ) · الفترة · المركبات المشمولة · الحالة.

TABLET (768px) note: replace the sidebar with a collapsed icon rail on the right; KPIs become 2×2; side panels become full-screen pages.
```

---

## PACKAGE 5 — Station Manager (mobile 390 + desktop 1440)

**Stitch mode:** Mobile first, then Web.

```
Design the ZOOD Station Manager portal for "محطة القاهرة الجديدة". User: مدير المحطة "عمرو حسن". The manager sees only this station.
Mobile bottom navigation (5): الرئيسية · المعاملات · الأجهزة · الورديات · المزيد.
Desktop grouped sidebar (right): الرئيسية · العمليات: المعاملات، الورديات · الشبكة: الأجهزة · المالية: المستحقات · الإعدادات.

Screens (mobile and desktop for each):

1. Home — 4 KPIs max: لترات اليوم 2,140 · معاملات اليوم 96 · الأجهزة المتصلة 3 من 4 · بانتظار المزامنة 2. "يحتاج انتباهك": "جهاز POS-NC-04 غير متصل منذ 45 دقيقة", "الوردية الصباحية لم تُغلق بعد". Chart: liters per hour today (single color). Last 4 transactions.

2. Transactions — Same list/table pattern as the company portal but columns relevant to the station: الوقت · المركبة · الشركة · الوقود · الكمية · المبلغ · الجهاز · الحالة. Billable vs VIP shown with a small chip.

3. Devices — Rows: device name (POS-NC-01..04), assigned agent, status (متصل / غير متصل منذ ...), last sync time, pending sync count, printer status (جاهزة / الورق منخفض). No technical IDs on the list.

4. Device details — Status, current agent, last sync, pending transactions, printer and battery state, today's transactions on this device. No edit actions (device activation is done by ZOOD Operations); one action: "الإبلاغ عن مشكلة".

5. Shifts — List of shifts: agent, start–end, transactions count, liters, status (مفتوحة / مغلقة).

6. Shift details — Agent, times, totals split "محتسبة" and "غير محتسبة (VIP)", pending sync, list of transactions; primary action "إغلاق الوردية" for open shifts.
```

---

## PACKAGE 6 — ZOOD Operations (desktop 1440 first + key mobile screens)

**Stitch mode:** Web, then Mobile for screens 1, 6, 7.

```
Design the ZOOD Operations console used by ZOOD staff (user: "سارة منصور — العمليات"). It monitors the whole network: companies, stations, devices, transactions, alerts.
Desktop grouped sidebar (right): الرئيسية · العملاء: الشركات، طلبات الشحن · الشبكة: المحطات، الأجهزة، مراقبة المزامنة · المعاملات · المخاطر: التنبيهات · المالية: مستحقات المحطات · الإدارة: سجل التدقيق.
Mobile bottom navigation (5): الرئيسية · الشركات · الشبكة · التنبيهات · المزيد.

Screens:

1. Home — 4 KPIs: حجم المعاملات اليوم 1.24M ج.م · الشركات النشطة 38 · المحطات النشطة 52 · أجهزة غير متصلة 3. Main chart: platform volume last 30 days. "يحتاج انتباهك": open alerts 7, pending top-ups 2, devices offline 3, reconciliation status "متطابق". A live "النشاط الآن" list of the latest 5 transactions across the network (company, station, amount, time).

2. Companies — Table: الشركة · الرصيد · المركبات · إنفاق الشهر · الحالة (نشطة / مجمدة). Row → company profile.

3. Company profile — Header with name and status, balance, actions "شحن المحفظة" and "تجميد الحساب". Tabs: نظرة عامة · المركبات · المعاملات · الطلبات. Overview shows balance trend, vehicles by tier, open requests.

4. Network — Stations list with connectivity profile chip (عادي / ضعيف الاتصال), devices online "3/4", today's liters. Station row → station detail with devices and fuel prices.

5. Sync monitor — Devices with pending offline transactions: device, station, last sync, pending count, oldest pending age, action "إعادة المحاولة".

6. Alerts queue — Unified list with type chips: لوحة غير مؤكدة · تعارض مزامنة · تجاوز سعة الخزان · فشل كتابة الشريحة · سعر قديم · سقف VIP. Columns: severity, type, vehicle/company, station, time, assignee, status.

7. Alert investigation — Left: alert summary and recommended actions ("تعليق الشريحة", "فتح حالة احتيال", "إغلاق التنبيه"). Right: the linked transaction with full details (amount, liters, vehicle tank capacity 50 لتر vs dispensed 62 لتر highlighted, station, device, agent, offline/online, device time vs server time vs sync time) and a vertical timeline of events (scan → validated → confirmed → tag written → synced). Plate photo thumbnail if available.

8. Top-up requests — Pending requests: company, amount, bank reference, requested at; action "تأكيد وإضافة للرصيد" with a confirmation dialog showing the amount clearly.
```
