# Testing report

**Environment:** Windows 11 developer laptop; JDK 25; embedded PostgreSQL 18 (Zonky); Node 22. No Docker. Every number below comes from a run on 2026-11-19 on branch `feature/zawed-production-hardening`.

## Commands and results

| Suite | Command | Result |
|---|---|---|
| API, full (unit, integration, RLS, concurrency, architecture, module boundaries, OpenAPI snapshot, migrations) | `cd apps/api && ./mvnw clean verify` | **311 tests, 0 failures, 0 errors, 0 skipped. BUILD SUCCESS.** Final run on the merge commit `043edbd`, migrations V1–V16 on a clean database. The two performance harnesses are opt-in (`-Dzawed.perf=true`) and were run separately (`performance-report.md`) |
| Web: typecheck, lint, unit tests (i18n parity, token contrast) | `cd apps/web && npm run check` | **204 tests passed**, lint and typecheck clean |
| Web production build | `npm run build` | passed |
| Web E2E (Playwright, core fueling flows on the demo build) | `npm run e2e` | **51 passed** |

## New tests in this branch

| Area | Test class | Tests | What it proves (against PostgreSQL state) |
|---|---|---|---|
| Top-ups | `ledger.TopUpTests` (rewritten) | 16 | The verified amount is credited, never the claim. The verifier and plain Ops cannot approve. The database refuses a single-step confirmation and rewriting a verification. A repeated approval and racing approvals credit once. A rejected top-up is never credited. Cancel rules; references unique per receiving account; transfer replay; reference variants; statement paging |
| Adjustments | `ledger.WalletAdjustmentTests` | 4 | Second-person approval. Posting against `platform:adjustments`. No self-approval. Idempotency-Key replay and mismatch (4 racing requests). Racing approvals post once |
| Company onboarding | `tenancy.CompanyIdempotencyTests` | 3 | Retry returns the same company; a different body is refused; 4 racing requests make one company and one wallet; keys are per user |
| Fleet identity | `fleet.FleetIdentityTests` | 6 | The Egyptian plate is one plate in any order. Other shapes are never merged. Fleet code is unique per company and freed by decommissioning. Tank and year bounds (including the database CHECK). Identity columns cannot change even for the owner. Racing registrations of one plate make one vehicle |
| Bulk import | `fleet.FleetImportTests` | 8 | Arabic CSV end to end with limits. XLSX. Bad files (type, encoding, too many rows, zip bomb, DOCTYPE, missing file). Problem rows and both modes, with a formula-defused error report. Re-upload creates nothing twice. A worker crash is resumed by another worker. Overlapping imports in two concurrent workers. The onboarding checklist |
| Retention | `network.NonceRetentionTests` | 2 | Old nonces purged and recent kept; the function refuses the replay window |
| Offline monitoring | `fueling.CompanyOfflineMonitoringTests` | 1 | One alert when crossing the company threshold across two devices; all captures still recorded |
| Migrations | `MigrationCompatibilityTests` | 2 | V11 database with legacy rows upgraded through V12+ (backfills checked, legacy confirmed top-ups stay final); a plate collision stops V14 |
| Updated | `AuthorizationMatrixTests`, `LedgerIsolationTests`, `ReconciliationTests`, `StationFinanceTests`, `PosTestSupport` | — | Every new endpoint has a matrix row (`OPS_MANAGER` included); old invariants re-asserted under the stricter V12 guard; funding now goes through maker-checker |

## Failed during development, then fixed (not suppressed)

| Failure | Cause | Fix |
|---|---|---|
| Flyway: function `topup_requests_guard` already exists | V6_1 had a guard of the same name | V12 drops and replaces it, before the backfill (the old guard would refuse updating confirmed rows) |
| Duplicate transfer answered 500 | A failed INSERT aborts the PostgreSQL transaction, so the replay lookup could not run | `ON CONFLICT DO NOTHING` |
| Company cancel of a verified request answered 404 | The new RLS update policy hides non-pending rows from `FOR UPDATE` | Read the status first and answer `TOPUP_NOT_PENDING` |
| Sign-in rate limit in a test | The funding helper's extra sign-in | Helper resets the limiter after its own sign-in |
| XLSX could not be read | `ZipInputStream` cannot read STORED entries with data descriptors | Archive inspection through `commons-compress` `ZipFile` |
| Two isolation tests | Stricter V12 guard messages, and RLS now hiding decided rows from the company | Assertions updated; the invariants are unchanged or stronger |

## Blocked or not executed
- **Load tests (k6):** NOT EXECUTED. k6 is not installed and software was not installed. The scripts and the in-JVM measurements are in `performance-report.md`.
- **Independent review by Fable (`zawed-reviewer`):** NOT DONE. The Fable model was out of usage credits during this work.
- **Manual browser walkthrough of the new screens:** not done. They are covered by type checks, lint and unit tests only.
- **Migration against a copy of the `zawed-dev` data:** not done (no copy available). Covered by `MigrationCompatibilityTests` and the pre-deploy queries in `deployment-guide.md`.
