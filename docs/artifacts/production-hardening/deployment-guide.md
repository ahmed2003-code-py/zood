# Deployment guide: production hardening

**Scope.** This branch is not deployed anywhere. Deploy only after review and merge into `develop`. The investor demo (`master`, Railway `zood-web`) is never touched.

## Prerequisites
1. **Review:**
   - a human review;
   - a `zawed-reviewer` pass on the money and security changes (V12, `TopUps`, `WalletAdjustments`, `IdempotencyKeys`, `FleetImportWorker`). It runs on Opus, because Fable is no longer available.
2. **Tests:** API and web suites green (`testing-report.md`).
3. **At least one Ops Manager, right after the deploy:**
   - The `OPS_MANAGER` role is added by V12 itself, so the user cannot exist before the deploy.
   - Immediately after the API and web are up, a Super Admin creates it (Users → New user, role `OPS_MANAGER`).
   - Until then, top-ups can be recorded and verified, but nobody can approve them.
   - The user enrols an authenticator at first sign-in.

## Configuration (all optional; defaults shown)

| Property | Default | Meaning |
|---|---|---|
| `zawed.finance.receiving-accounts` | `MAIN` | ZAWED's receiving bank accounts (comma separated codes, `[A-Z0-9_]`) |
| `zawed.sync.company-offline-alert-piastres` | 2000000 | Company-wide offline monitoring threshold (24 h) |
| `zawed.pos.nonce-retention` | `PT1H` | Nonce age before purge (minimum 15 min) |
| `zawed.pos.nonce-purge-interval` | `PT15M` | How often the purge runs |
| `zawed.fleet-import.worker-enabled` | `true` | The import worker on this instance |
| `zawed.fleet-import.poll-interval` | `PT2S` | Worker poll interval |
| `spring.servlet.multipart.max-file-size` | 5MB | Upload limit (set in `application.yaml`) |

## Before migrating an environment with data
1. **Back up the database** (Railway PostgreSQL backup or `pg_dump`).
2. **Plate collisions.** V14 stops if two vehicles in service would share a canonical plate key. Check first:

   ```sql
   SELECT plate_key_c, array_agg(id) FROM (
     SELECT id, CASE WHEN plate_key ~ '^[0-9]+[^0-9]+$' OR plate_key ~ '^[^0-9]+[0-9]+$'
                     THEN regexp_replace(plate_key, '[^0-9]', '', 'g') || regexp_replace(plate_key, '[0-9]', '', 'g')
                     ELSE plate_key END AS plate_key_c
     FROM vehicles WHERE status <> 'DECOMMISSIONED') v
   GROUP BY plate_key_c HAVING count(*) > 1;
   ```

   Every row returned is the same physical plate registered twice. Decommission the wrong one before deploying.
3. **Vehicles over 1,500 L.** The tank ceiling check fails on such rows. Check with `SELECT id FROM vehicles WHERE tank_capacity_ml > 1500000;` and correct them first.
4. **Pending top-ups** stay `PENDING`, and Ops verify them in the new flow.

## V16 index build
V16 builds three indexes on `fuelings` inside Flyway's transaction, and inserts into `fuelings` wait while it runs.
- **On today's data:** a few seconds.
- **On a large table:** about 2 s per million rows per B-tree on the test laptop.
- **When that pause matters:** switch to concurrent builds, as described in the V16 header:
  1. set `spring.flyway.postgresql.transactional-lock: false`;
  2. add the `CONCURRENTLY` keyword;
  3. add a `.conf` file with `executeInTransaction=false`.

  Do this in a new migration, never by editing V16 once it is applied.

## Order
1. Deploy the API (Flyway applies V12 → V16 at startup, in one run).
2. Deploy the web, which uses the new endpoints.
   - In between, the old web cannot confirm top-ups, because `/confirm` is gone. Deploy the two back to back, or deploy the API first during a quiet hour.

## Monitoring
- **Logs:**
  - `Purged N device nonces` every 15 minutes, while devices are active;
  - `Fleet import … finished`;
  - `An uploaded spreadsheet … could not be read` (file problems; no content is logged).
- **Alerts:** `COMPANY_OFFLINE_EXPOSURE` in the Ops alerts queue.
- **Database:** `SELECT status, count(*) FROM fleet_import_jobs GROUP BY 1;`. A job `RUNNING` with `lease_until` long past means no worker is running.
- **Reconciliation:** keeps running nightly. `platform:adjustments` is a platform account and is not reconciled (no cached balance).

## Recovery and rollback
- **Migrations are forward-only.** A failed V14 (a plate collision) rolls back and leaves V13 applied. The API then fails to start, and Railway keeps the previous deployment serving. Fix the data and redeploy.
- **The code depends on V12+ (new columns and tables),** so after V12 is applied the old code cannot simply be redeployed. Roll forward with a fix.
- **The new data is additive:** wallet adjustments, idempotency keys, import jobs. Nothing existing is deleted or rewritten, except plate keys, which are recomputable from `plate` and the documented rule.
- **A stuck import:** let the lease expire, and another instance resumes it. Or set `status = 'COMPLETED_WITH_ERRORS', finished_at = now()` as the owner after review; the vehicles already created stay.
