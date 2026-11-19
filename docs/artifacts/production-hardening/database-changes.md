# Database changes: production hardening

Branch `feature/zawed-production-hardening` · PostgreSQL 18 · forward-only Flyway migrations (no applied migration was edited).

## Change log

| Version | File | Tables | What changed | Why | ADR |
|---|---|---|---|---|---|
| V12 | `V12__maker_checker_funds.sql` | `users`, `topup_requests`, `ledger_accounts`, `wallet_adjustments` (new) | See below | Audit F1, F2 | 0021 |
| V13 | `V13__idempotency_keys.sql` | `idempotency_keys` (new) | Request idempotency | Audit F3 | 0022 |
| V14 | `V14__fleet_identity_and_retention.sql` | `vehicles`, `rfid_tags`, `device_nonces`, `fuelings`, `alerts` | Identity triggers, tank ceiling, `fleet_code`, canonical plate keys, nonce purge, company offline index | Audit H1, H5, H6, F5, F7 | 0023, 0024, 0025 |
| V15 | `V15__fleet_imports.sql` | `fleet_import_jobs`, `fleet_import_rows` (new) | Bulk vehicle import | Audit F4 | 0026 |
| V16 | (performance branch, pending) | `fuelings` | Time indexes, only if `EXPLAIN` justifies them | Audit F6 | n/a |

### V12: maker-checker funds
- **`users`:**
  - `users_role_check` and `users_role_matches_scope` accept `OPS_MANAGER` (platform scope).
- **`topup_requests`, new columns:**
  - `origin` (`COMPANY_REQUEST` | `OPS_RECORDED`) and `receiving_account` (default `MAIN`);
  - `verified_amount_piastres`, `verified_by`, `verified_at`, `evidence_reference`;
  - `legacy_single_step`.
- **`topup_requests`, constraints:**
  - status adds `PENDING_APPROVAL`;
  - `topup_requests_decided` now treats `PENDING_APPROVAL` as undecided;
  - new checks `topup_requests_verified`, `topup_requests_verified_before_approval`, `topup_requests_two_person` and `topup_requests_ops_recorded_is_verified`.
- **`topup_requests`, index:** `topup_requests_reference_live_key` is now on (`receiving_account`, `bank_reference_key`) for live statuses; new `topup_requests_awaiting_approval_idx`.
- **`topup_requests`, RLS:** `topup_requests_decide` lets a company update only a `PENDING` row.
- **`topup_requests`, triggers:**
  - The V6_1 `topup_requests_guard` is replaced by one that keeps all its rules, and adds:
    - new rows start pending and are never legacy;
    - the verifier and decider are `app_user_id()`;
    - a verification is written once;
    - the transfer date changes only at verification;
    - only a verified row is confirmed.
  - `topup_requests_no_delete` is added.
- **Backfill:**
  - rows without a requester become `OPS_RECORDED`;
  - `CONFIRMED` rows get `verified_* = amount/decider` and `legacy_single_step = true`.
- **`ledger_accounts`:**
  - kind and owner checks accept `PLATFORM_ADJUSTMENT`;
  - the row `platform:adjustments` is inserted.
- **`wallet_adjustments`:**
  - **checks:** two-person, cancelled by the requester, approved has a posting, rejection has a reason;
  - **guard trigger:** the requester and decider are the acting user, and the request is immutable;
  - **no delete;**
  - **RLS:** platform only;
  - **grants:** column-level `UPDATE`.

### V13: idempotency keys
`idempotency_keys` (`actor_id`, `operation`, `key`) primary key, `request_hash`, `resource_id`. RLS: own keys only. Grants: `SELECT` and `INSERT` only.

### V14: fleet identity and retention
- **Identity trigger:** `fleet_identity_guard` on `rfid_tags` (company, vehicle and UID columns) and `vehicles` (`company_id`).
- **Tank ceiling:** `vehicles_tank_capacity_ceiling` (≤ 1,500,000 ml), added `NOT VALID` then validated.
- **Fleet code:** `vehicles.fleet_code`, with partial unique `vehicles_fleet_code_key` (`company_id`, `upper(fleet_code)`) for vehicles in service.
- **Plate keys:**
  - `plate_key_canonical()` is added;
  - a collision check stops the migration if two vehicles in service would share a key;
  - existing keys are backfilled.
- **Nonce purge:** `purge_device_nonces(timestamptz, int)`, `SECURITY DEFINER`, refuses inside 15 minutes, `EXECUTE` granted to `zawed_app`.
- **Offline monitoring:** `fuelings_company_offline_idx` (`company_id`, `evaluated_at`) where origin is `OFFLINE`, and `alerts_type_check` adds `COMPANY_OFFLINE_EXPOSURE`.

### V15: fleet imports
`fleet_import_jobs` and `fleet_import_rows`:
- **checks:** counts, confirmed, strict-is-clean, finished, and imported-has-vehicle;
- **partial unique index:** `fleet_import_jobs_one_active`, one queued or running import per company;
- **RLS:** platform only;
- **grants:** column-level `UPDATE`.

## Compatibility and deployment
- **Run order:** V12 → V15 run in order on startup (Flyway). All are additive except the replaced trigger and index, and those keep the old rules.
- **Data compatibility:** `MigrationCompatibilityTests` migrates a database to V11, plants legacy rows, then applies V12+.
  - The legacy backfills are verified.
  - A legacy confirmed top-up stays final.
  - A plate collision stops V14 with the ids of the vehicles involved.
- **Before deploying to an environment with data:** run the collision query in `deployment-guide.md`. A collision must be resolved, by decommissioning the duplicate, before V14 can run.
- **Lock duration:** small tables, so the `ALTER TABLE` locks are brief. The tank check is validated separately, as the online-safe pattern for large tables.

## Validation performed
- The full API suite, including Flyway on a clean database (see `testing-report.md`).
- `MigrationCompatibilityTests`: upgrade from V11 with legacy data, and the collision refusal.
