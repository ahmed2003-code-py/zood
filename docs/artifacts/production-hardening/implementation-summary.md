# Implementation summary: production hardening

Branch `feature/zawed-production-hardening` (from `develop` at `ba056b3`). Not merged and not deployed.

## Completed

| Area | What was built | Key files |
|---|---|---|
| Maker-checker top-ups (F1) | Verify step with the bank-received amount, receiving account and evidence; approval by a different user holding `TOPUP_APPROVE` (new `OPS_MANAGER` role); only the verified amount is credited | `V12`, `ledger/TopUps.java`, `ledger/OpsWalletController.java` |
| Controlled direct credits (F2) | Recorded transfers wait for approval; non-bank credits are wallet adjustments (reason, second person, `platform:adjustments`) | `V12`, `ledger/WalletAdjustments.java`, `ledger/Ledger.java` |
| Idempotent onboarding (F3) | `Idempotency-Key` bound to the request body, stored with the resource in one transaction | `V13`, `platform/persistence/IdempotencyKeys.java`, `tenancy/Companies.java` |
| Bulk import (F4) | CSV and XLSX upload, validation, preview, strict or valid-rows confirmation, PostgreSQL-backed worker with leases, crash recovery, templates, error report | `V15`, `fleet/FleetImport*.java` |
| Nonce retention (F5) | Bounded purge every 15 minutes (keeps one hour) | `V14`, `network/NonceRetention.java` |
| Fueling time indexes (F6) | Three indexes justified by `EXPLAIN` on 5 M rows | `V16` |
| Offline exposure (F7) | Company-wide monitoring alert across devices; cap documented as a proposal | `V14`, `fueling/FuelingEngine.java`, ADR 0025 |
| Fleet identity (H1, H5, H6) | Immutable tag and vehicle identity, tank and year bounds, `fleet_code`, canonical Egyptian plate key | `V14`, `fleet/Vehicles.java`, `fleet/PlateKey.java` |
| Onboarding readiness | Checklist derived from data | `fleet/FleetImportsController.java` (`/ops/companies/{id}/onboarding`) |
| Web | Verify and approve screens, adjustments, readiness panel, import page, fleet code, `OPS_MANAGER` | `apps/web/src/live/ops/*`, `live/wallet.tsx`, `auth/portals.ts` |
| Tests | 26 tests in 7 new classes (including 2 migration upgrade tests), 16 rewritten top-up tests, matrix rows for every endpoint; 2 opt-in performance harnesses | `apps/api/src/test/...` |
| Documentation | ADRs 0021–0026, ERD (English) updated to V16, these artifacts, performance evidence, k6 scripts | `docs/adr`, `docs/architecture`, `docs/artifacts/production-hardening`, `tools/load` |

## Important decisions
- **One new role, `OPS_MANAGER`, instead of a role hierarchy.** Plain Ops verifies; only a manager or Super Admin approves (ADR 0021).
- **Bank references are unique per ZAWED receiving account**, not globally.
- **Adjustments are credits only.** Debits stay reversals.
- **No onboarding state machine.** Readiness is computed from the data.
- **No `vehicle_tag_assignments` table.** Immutable tag rows are the history (ADR 0023).
- **Plate uniqueness stays platform-wide among vehicles in service.** The canonical form applies only to the Egyptian shape, and the migration stops on a collision rather than merging.
- **Import:** `STRICT` by default, partial import only by an explicit choice, PostgreSQL as the queue (ADR 0026).
- **Offline:** monitoring only. No cap is claimed that a disconnected device could not enforce (ADR 0025).
- **V16 builds its indexes inside Flyway's transaction:** the concurrent build hangs under Flyway's default lock (the documented switch is in `deployment-guide.md`).

## Remaining limitations
- **Fable review not done:** an independent review by the Fable reviewer is still needed for the money and security changes.
- **Not run:** load tests with k6, and a manual browser walkthrough of the new screens.
- **Lists with an old date range** still take about 1.6 s at 5 M rows: they need an `evaluated_at` cursor (a measured rewrite, a separate change).
- **Owner decisions:** a company receivable ceiling for offline, commercial registration uniqueness, and plate category.
- **The Arabic guide page** (artifact) predates V12–V16. The repository ERD is current.
