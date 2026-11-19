# Final handover: production hardening

**Branch:** `feature/zawed-production-hardening` (pushed to `origin`; not merged, not deployed) · **Base:** `develop` at `ba056b3` · **Date:** 2026-11-19

> **الخلاصة بالعربي.** اتصلح الخلل الأساسي في الفلوس: الشحن بقى بيضيف المبلغ اللي وصل فعلًا للبنك، وبموافقة شخص تاني. كمان:
> - مفيش أي إضافة رصيد بقت ممكنة بشخص واحد؛
> - إنشاء الشركة بقى ما يتكررش لو الطلب اتعاد؛
> - استيراد العربيات بالجملة من Excel أو CSV شغال، وبيكمّل لو حصل crash من غير تكرار؛
> - البيانات اتقفلت ضد التعديل الغلط.
>
> كل الاختبارات نجحت: 311 للـ API، و204 + 51 للويب. الحاجات اللي لسه:
> - مراجعة Fable؛
> - تجربة الشاشات في المتصفح؛
> - load test بـ k6؛
> - قرارين عند صاحب المشروع.

## Executive summary
The audit's critical finding (F1) is fixed:
- **Top-ups:** a top-up credits the amount verified on ZAWED's bank statement, only after a second person approves.
- **No single-person money:** every wallet credit now goes through maker-checker, enforced in both the service and the database.
- **Company creation** is idempotent.
- **Bulk import:** operations can import thousands of vehicles from CSV or XLSX, with preview, explicit confirmation and crash-safe background processing.
- **Integrity and scale:** identity columns are protected, plate duplicates are caught across typing orders, nonces are purged, and fueling date reads are indexed from measured evidence.

**This is ready for review, not for production.** Before production it needs an independent Fable review of the money and security code, a browser walkthrough, and an open-loop load test.

## Commits (all pushed)

| Commit | Content |
|---|---|
| `4be1963` | P0: V12 maker-checker funds and `OPS_MANAGER`, V13 idempotency keys, V14 fleet identity, nonce retention, offline monitoring, tests |
| `f570025` | V15 bulk import, onboarding readiness, migration compatibility tests, ADRs 0021–0026, ERD, artifacts |
| `9fdd0fd` | Web: verify and approve, adjustments, readiness panel, import page, `OPS_MANAGER` |
| `02b77da`, `fe22424` | Performance branch: V16 indexes, query-plan harness, in-JVM load measurement, report, k6 scripts |
| `043edbd` | Merge of the performance branch |
| final commit | Testing report, implementation summary, this handover |

## What changed

### Database (details in `database-changes.md`)
- **V12:** maker-checker top-ups, `OPS_MANAGER`, `wallet_adjustments`, `platform:adjustments`, guard triggers.
- **V13:** `idempotency_keys`.
- **V14:**
  - immutable identity columns, tank ceiling, `fleet_code`;
  - canonical plate keys (with a collision stop);
  - nonce purge function;
  - company offline index and alert.
- **V15:** `fleet_import_jobs` and `fleet_import_rows`.
- **V16:** three fueling time indexes.

### Backend
- **ledger:** `TopUps` (rewritten), `WalletAdjustments` (new), `Ledger` (new account kind), `OpsWalletController`.
- **platform:** `IdempotencyKeys`, `Permission` (`TOPUP_APPROVE`), `Role` (`OPS_MANAGER`).
- **tenancy:** `Companies` (idempotent create).
- **fleet:**
  - `Vehicles` (fleet code, validation), `PlateKey`;
  - new: `FleetImportFile`, `FleetImports`, `FleetImportWorker`, `FleetImportsController`.
- **network:** `NonceRetention` (new).
- **fueling:** `FuelingEngine`, `Fuelings`, `Alerts` (company offline monitoring).

### APIs
See `api-changes.md`. The breaking changes are in the Ops console only: `/topups/{id}/confirm` is replaced by `/verify` and `/approve`, and the wallet credit is now recorded and then approved. The POS contract is unchanged.

### Frontend
- **Ops top-ups and adjustments:** verify, then approve by a different manager; claimed and verified amounts are shown side by side.
- **Company page:** readiness checklist, and the adjustment request.
- **Import page:** full flow.
- **Other:** fleet code on vehicles; `OPS_MANAGER` everywhere roles are listed; all new strings in Arabic and English.

### Security and concurrency
- **Two-person rules:** enforced in the database (CHECK constraints, plus triggers binding the verifier and approver to `app_user_id()`).
- **RLS on the new tables:** adjustments and imports are platform-only; idempotency keys are per user. A company can withdraw only an unverified claim.
- **Import worker:** a reviewed platform-scope entry, acting for the confirming user.
- **Upload hardening:** size, row count, encoding, zip-bomb and DOCTYPE checks, header validation, and CSV formula defusing.
- **Lock order:** documented and verified (`concurrency-report.md`). No global lock was added.

## Test results (see `testing-report.md`)
- **API:** 311 tests, 0 failures (final run on `043edbd` with V1–V16).
- **Web:** `npm run check` (204 tests), `npm run build` and `npm run e2e` (51 passed).
- **Migrations:** an upgrade from V11 with legacy data, and the plate collision stop, both tested.

## Performance (see `performance-report.md`)
- **Dataset:** 5,000,000 synthetic fuelings and 200,000 vehicles on a developer laptop (embedded PostgreSQL, `fsync` off).
- **With V16:**
  - the largest company's monthly calendar went from 4.18 s to 0.16 s;
  - the operator export count went from 0.75 s to 0.07 s;
  - engine reads stay under 1.1 ms.
- **In-JVM load:** 340.6 online fuelings/s, p95 124.5 ms (p95 172.8 ms on one hot wallet), 0 errors, 0 deadlocks; every wallet and payable reconciles to the piastre.
- **Not executed:** the k6 scripts, and any test on production-like hardware.

## Known limitations and remaining risks
1. **No independent review** by the Fable reviewer (out of usage credits). Required before production for V12, `TopUps`, `WalletAdjustments`, `IdempotencyKeys`, `FleetImportWorker` and the import parser.
2. **New screens not walked through in a browser.** They are covered by type checks, lint and unit tests only.
3. **Lists with an old date range** take about 1.6 s at 5 M rows. They need an `evaluated_at` cursor (a measured rewrite, its own change).
4. **Offline exposure across devices is monitored, not capped.** A receivable ceiling needs an owner decision (ADR 0025).
5. **V16 builds its indexes with a short write pause** on `fuelings`. The concurrent route is documented.
6. **The Arabic copy** in the new screens has not been reviewed by a native speaker.
7. **The shareable Arabic and English guide pages** predate V12–V16. The repository ERD is current.
8. **Disk space:** the performance agent left a 7.4 GB synthetic database at `D:\zawed-perf\pgdata`, outside the repository. Delete it when no longer needed.

## Recommended next steps for the owner
1. **Decide:** the offline receivable ceiling, whether commercial registration must be unique, and whether plate category is part of identity.
2. **Run the Fable review** on this branch once credits are available, and fix its findings.
3. **Walk through the new Ops screens** on a local or `zawed-dev` build after review: verify, approve, adjustment, import with the Arabic template.
4. Run the pre-deploy queries in `deployment-guide.md`, merge into `develop`, deploy the API and the web back to back, then **create at least one `OPS_MANAGER` user** (the role arrives with V12).
5. **Run the k6 scripts** (`tools/load/`) against `zawed-dev` and compare with the proposed targets.
6. **Schedule the list-cursor change** (old date ranges) as its own small phase.
