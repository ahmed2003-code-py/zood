# Fleet onboarding guide

Written for Operations users and the developers who support them.
- **دليل تسجيل الشركات والأسطول.** الخطوات من إنشاء الشركة لحد ما تبقى جاهزة تموّن.

## 1. Create the company
1. **Ops console → Companies → New company:** legal name, Arabic and English display names, tax id, commercial registration and contact.
   - The wallet is opened in the same transaction, so a company never exists without one.
   - The form sends an `Idempotency-Key`: a double click or a retry after a timeout returns the same company instead of creating a second one.
2. **Departments** (optional): add them before importing vehicles, because import rows match departments by name.
3. **Fueling policy:** the cooldown (120–240 minutes) and the low-balance threshold. Changing them later is a Super Admin action.

## 2. Check readiness
The company page shows the **onboarding checklist** (`GET /ops/companies/{id}/onboarding`), worked out from the data with nothing stored.

| Check | Required to fuel | Done when |
|---|---|---|
| ACTIVE | yes | The company is not frozen |
| VEHICLES | yes | At least one active vehicle |
| TAGS | yes | Every active vehicle has an active tag |
| WALLET_FUNDED | yes | Balance > 0 (an approved top-up) |
| CONTACT, REGISTRATION, DEPARTMENTS, LIMITS, COMPANY_USERS | recommended | As named |

`ready` is true when every required check is done.

## 3. Register vehicles one by one
**Company → Vehicles → New vehicle.**

**Fields:**
- plate (as on the licence; shown exactly as typed);
- optional fleet code (the company's own number);
- make, model, year;
- fuel type, tank capacity, department, tier.

**Rules:**
- A plate is unique among vehicles in service across the platform. The digits and letters of an Egyptian plate are matched in any typing order, with Arabic or Western digits.
- The fleet code is unique within the company among vehicles in service.
- The tank is at most 1,500 L; the year runs from 1950 to next year.
- A VIP monthly ceiling applies to VIP vehicles only.

**Lifecycle:**
- ACTIVE ⇄ SUSPENDED;
- → DECOMMISSIONED is final: it deactivates the vehicle's tags and limits, and frees the plate and the fleet code.

**Tags:** issued per vehicle by the installer. One active tag per vehicle, and a UID belongs to one vehicle for ever.

## 4. Bulk import (many vehicles)
1. **Download the template:** Arabic or English, XLSX or CSV.

   | Column (English / Arabic) | Required | Values |
   |---|---|---|
   | plate / اللوحة | yes | As registered |
   | fleet_code / كود الأسطول | no | Up to 40 characters |
   | make / الماركة | yes | Up to 60 |
   | model / الموديل | yes | Up to 60 |
   | year / سنة الصنع | no | 1950 to next year |
   | fuel_type / نوع الوقود | yes | GAS_80, GAS_92, GAS_95, DIESEL, CNG, or بنزين 80, بنزين 92, بنزين 95, سولار, غاز طبيعي |
   | tank_capacity_l / سعة التانك (لتر) | yes | Litres, up to 3 decimals, at most 1,500 |
   | department / الإدارة | no | Name of an existing department of this company |
   | tier / الفئة | no | NORMAL / عادي (default) or VIP |
   | vip_monthly_ceiling_l | no | VIP only |
   | daily_limit_l, weekly_limit_l, monthly_limit_l | no | Litres |

2. **The company fills it in and sends it back.** At most 5,000 vehicles and 5 MB per file; split larger fleets. Delete the example row.
3. **Upload:** Company → Import vehicles.
   - The file is checked and every row validated. **Nothing is created yet.**
   - Unknown or missing columns, a wrong encoding, or an unsafe or oversized file are refused with a clear message.
4. **Preview:** the counts, plus every row with a problem and its column and reason:
   - a missing value, a bad fuel type or tank, an unknown department;
   - a duplicate inside the file;
   - a plate or fleet code already in service.

   Download the error report (CSV) to send back to the company.
5. **Confirm:**
   - **Strict (default):** possible only when every row is valid. The safest choice.
   - **Valid rows only:** must be chosen explicitly. Problem rows are skipped and listed.
6. **Processing:** in the background, about 250 rows per tick. The page shows the progress. Only one import per company runs at a time.
7. **Results:** imported, skipped, and rows that became `EXISTS` (registered by someone else meanwhile).

## 5. Errors and recovery
- **Re-uploading a file already imported** is flagged (`sameFileImportedBefore`). Its rows show `PLATE_IN_USE`, and nothing is created twice.
- **If the API restarts during an import,** the job is resumed by the next worker once its lease expires (2 minutes). Each vehicle and its row outcome are saved together, so nothing is lost or duplicated.
- **An unconfirmed import** can be cancelled. A confirmed one cannot: it only creates vehicles, which can be decommissioned one by one.

## 6. Fund the wallet
1. **The company transfers money to ZAWED's account.** It may report it in its portal; that report is a claim, not proof.
2. **An Ops user finds the transfer on ZAWED's bank statement** and **verifies** it: the receiving account, the reference as printed, the **amount that actually arrived**, the value date and an evidence reference. If the company did not report it, use **Record bank transfer** on the company page.
3. **An Ops Manager (a different person) approves.** Only then is the wallet credited, with the verified amount, once.
4. **A credit that is not a bank transfer** (goodwill, a correction) is a **wallet adjustment**: requested with a reason and approved by someone else.
