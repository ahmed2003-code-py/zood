# Production-readiness audit: onboarding, tenant integrity, concurrency (Phase A)

**Date:** 2026-11-19 · **Branch:** `develop` at `ba056b3` · **Schema:** V1–V11 · **Status:** audit only. No schema or code was changed for this document.

**Method.** I read the migrations, the services on the money path (`ledger`, `fueling`, `stationfinance`), `fleet`, `tenancy`, the security and scope classes, the indexes and grants, and the existing tests. Every finding below names the file it comes from.

**Not done in Phase A:**
- No `EXPLAIN ANALYZE` on a large dataset.
- No load test.
- No new concurrency tests.
- No review by the Fable reviewer (`zawed-reviewer`), which is out of usage credits. This audit was done on Opus, and the money-path findings should get a Fable review before they ship.

---

## 1. Summary

The core is sound and mostly already built for this scale:
- an immutable double-entry ledger with database-enforced balance;
- a fixed lock order;
- idempotent fueling;
- RLS bound to a non-owner runtime role;
- same-tenant triggers;
- two-person controls enforced by CHECK constraints.

No global lock serialises unrelated companies: `platform:bank` has no cached balance and is never locked.

What blocks production for the stated business model:

| # | Finding | Severity |
|---|---|---|
| F1 | **A confirmed top-up credits the amount the company typed**, not the amount Operations verified on the bank statement. | **Critical** |
| F2 | A direct wallet credit by Operations is a single-person action with no amount threshold. Every other way money leaves or enters has two-person control above a threshold. | High |
| F3 | Company creation is not idempotent. A retried `POST /ops/companies` without a tax id creates a second company. `Idempotency-Key` is allowed by CORS but not implemented. | High |
| F4 | No bulk vehicle import exists (API or web). Onboarding 1,000–2,000 vehicles means 1,000–2,000 manual forms. | High (business) |
| F5 | `device_nonces` grows forever (one row per device token, every ≤15 min). At about 1,000 devices that is roughly 35 M rows a year. | High (operations) |
| F6 | Date-range reads on `fuelings` (calendar, exports, Ops lists with `from/to`) have no index on time. They will degrade as history grows. Needs measuring. | Medium, likely High at scale |
| F7 | The offline exposure bound is per device and company (10,000 EGP / 24 h). It is not a global bound per company: N devices give N× exposure. | Medium (policy) |

Everything else is either correct, or a small hardening item (section 4).

---

## 2. Payment model fit (manual bank transfer)

| Requirement | Today | Verdict |
|---|---|---|
| A submitted top-up is not proof of payment | A request is `PENDING` and posts nothing; only Ops confirmation posts (`ledger/TopUps.java:180`) | ✅ correct |
| Credit = the **verified received** amount | `confirm()` posts `topUp.amountPiastres()`, the company's number (`TopUps.java:199-201`). Ops can correct the reference but not the amount | ❌ **F1** |
| A transfer credits once | Normalised `bank_reference_key` is unique among `PENDING`/`CONFIRMED` (`V6:130`, NFKC plus digit folding in `V6_1`); the ledger source is unique (`ledger_transactions_source_key`) | ✅ correct |
| Two Ops users confirming at once | Request row lock, then wallet lock, then a conditional `status = 'PENDING'` flip (`TopUps.java:182-186, 314-318`); covered by `TopUpTests.theSameBankReferenceNeverCreditsTwice` | ✅ correct |
| The verifier is recorded | `decided_by`, `decided_at`, and an audit row with `bankReferenceRequested` when the reference was corrected | ✅ correct |
| Evidence reference | Not stored (only a free `note`) | ➖ gap (small) |
| The requester cannot verify | The requester is a company user; the verifier needs `WALLET_CREDIT` (platform only) | ✅ correct |
| Direct credit (no company request) | One Ops user, any amount up to the cap, no second person (`TopUps.java:229`) | ⚠️ **F2** |
| No payment gateway, no automatic money movement | None exists | ✅ correct |
| Commercial agreement metadata | None | Not needed for correctness. Recommendation: at most one optional `companies.agreement_reference`; no billing subsystem. |

---

## 3. Gap analysis

### 3.1 Already implemented correctly

- **Ledger:**
  - balanced transactions (deferred constraint triggers in `V6:210-213`);
  - append-only entries;
  - one posting per source (`ledger_transactions_source_key`);
  - one reversal per transaction (`ledger_transactions_reversed_once`, V9);
  - cached balances updated under row locks with `balance_after_piastres`;
  - wallet ≥ 0 (CHECK);
  - nightly reconciliation of wallets and payables.
- **Lock order** wallet → vehicle (share) → tag → payable, documented in `ledger/Ledger.java:20-28` and followed by fueling, reversal (wallet, then payable through `ledger.reverse`), payout (payable only) and top-up (request row, then wallet). A deadlock on fueling is retried once (`FuelingEngine.java:487-494`).
- **Fueling idempotency:** `client_txn_id` unique; the same id from another device gives `CLIENT_TXN_CONFLICT`; another tag or volume gives `IDEMPOTENCY_MISMATCH` (`FuelingEngine.java:620-626`).
- **Fuelings immutable** for every role (V7_1), with a same-tenant trigger across company, vehicle, tag, operator, station, device and agent.
- **Tenant isolation:**
  - the app never runs as the owner (`connection-init-sql: SET ROLE zawed_app`);
  - `ScopedTransactionManager` refuses any other role and sets the scope transaction-local;
  - all `SECURITY DEFINER` functions pin `search_path` (21 of 21 checked);
  - scope escalation is a closed list enforced by `ArchitectureTests`.
- **Vehicles:**
  - plate key unique among non-decommissioned vehicles (`vehicles_plate_key`);
  - a decommissioned vehicle cannot be reactivated (`Vehicles.java:238`);
  - decommissioning deactivates tags and limits in the same transaction (`Vehicles.java:249-258`).
- **Tags:**
  - one `ACTIVE` tag per vehicle (`rfid_tags_one_active_per_vehicle`);
  - UID lookup unique for ever (`rfid_tags_uid_lookup_key`), so a UID can never point at two vehicles;
  - tag changes lock the vehicle first (`Tags.java:113, 176`);
  - the status transitions are an explicit table (`Tags.java:70-76`).
- **Limits:** one active rule per (vehicle, kind, period) (`limit_rules_one_active`).
- **Company creation opens its wallet in the same transaction** (`Companies.java:218`), so a company without a wallet never commits.
- **Station finance:**
  - `available − pending` under the payable lock;
  - payout reference unique among live payouts;
  - one live reversal per fueling;
  - dual control by CHECK (V9).

### 3.2 Implemented but insufficiently protected

| Item | Detail | Proposal |
|---|---|---|
| Identity columns of `rfid_tags` and `vehicles` | `zawed_app` has table-wide `UPDATE` on these tables (`V4:243`). No code changes `rfid_tags.vehicle_id`, `company_id` or `uid_*`, or `vehicles.company_id`, but nothing refuses it. The same-tenant trigger would still refuse a cross-company move, but not moving a tag to another vehicle of the same company, which would rewrite the tag's assignment history. | A trigger that refuses changes to these columns (or column-level grants). Makes "a tag row is one assignment, for ever" a database invariant, which removes the need for a separate assignment table (section 5.3). |
| Idempotency payload binding | Online replay compares tag and volume only. The same id with another `fuelType` replays the first answer. Offline replay ignores `deviceAt`. | Include `fuelType` (online) and `deviceAt` (offline) in the comparison. |
| `sync_batches` | No uniqueness on (`device_id`, `device_batch_id`): a resent batch is recorded twice in the monitor. Items are idempotent, so there is no money impact. | A unique index, and skip re-recording the batch. |
| Company identity | Only `tax_id` is unique; `commercial_registration` is not normalised or unique; no idempotency on create (F3). | `Idempotency-Key` on `POST /ops/companies`; a partial unique index on normalised commercial registration (owner to confirm). |
| Retries on finance operations | Only fueling retries a deadlock. Payout, reversal and top-up rely on lock order (correct today), so a rare deadlock would surface as a 500 with nothing posted. | Shared retry-once wrapper for deadlocks and serialization failures on the money commands. |
| Multi-instance behaviour | The rate limiter is in memory (`platform/ratelimit/InMemoryRateLimiter`), so limits apply per instance. `@Scheduled` reconciliation would run on every instance (harmless, just duplicated). | Fine for one instance. Before scaling out: limiter in PostgreSQL (or Redis only if measured), and a "one runner" guard (`pg_try_advisory_xact_lock`) for scheduled jobs. |
| Vehicle tank capacity | `> 0` only; no upper bound. A typo of 600,000 L is accepted. | Upper bound 1,500,000 ml (the same ceiling as a fueling's volume). |

### 3.3 Missing

1. **Received amount and evidence on top-up confirmation** (F1).
2. **Dual control for large direct credits** (F2).
3. **Idempotent company creation** (F3).
4. **Bulk vehicle import** (F4): template, upload, validation preview, confirm, asynchronous execution, results, retry.
5. **An onboarding readiness view for a company:**
   - Is the contact complete? Is the policy set?
   - How many vehicles are there, and how many have an active tag?
   - Is the wallet funded? Have company users been invited?

   Nothing tells Ops whether a company is ready.
6. **Optional fleet code** (the company's own vehicle number), for matching import rows and for fleet managers. It does not exist.
7. **Nonce purge** (F5) and other retention jobs.
8. **A time index on fuelings** (F6), pending measurement.
9. **Concurrency tests** for:
   - two fuelings on the same vehicle or tag;
   - fueling during suspension, limit change or top-up;
   - two payouts at once;
   - a payout during a reversal;
   - concurrent tag issue;
   - concurrent imports.

   Today's race tests cover: the same `client_txn_id`, a duplicate ledger source, the first receivable, the same bank reference, and refresh-token rotation.
10. **Load-test tooling.** None in the repository (no k6 or Gatling), and no Docker on the dev machine.
11. **A database change log.** History exists only in the migrations and the phase summaries.

### 3.4 Potentially unsafe

| Item | Why | Proposal |
|---|---|---|
| F1 | Money credited on an unverified number | Must fix |
| F2 | One person can create money in a wallet | Must fix (threshold is an owner value; safe default 50,000 EGP like payouts) |
| Plate key ordering | `PlateKey.of` keeps characters in input order: `١٢٣٤ ط ب ع` and `ط ب ع 1234` give different keys, so a duplicate can slip in. It never merges two different plates; the error is only a missed duplicate. | Canonical key = digit group + letter group (reading order kept within each group), with a collision report before the backfill. Research: whether plate category (private, taxi, transport) must be part of identity. |

### 3.5 Unnecessary or overengineered for now

- **A `vehicle_tag_assignments` table.** Each `rfid_tags` row already *is* one assignment (the UID is unique for ever, and the row names one vehicle). The missing piece is making `vehicle_id` immutable (3.2), not a new table.
- **New company status values** (DRAFT … ACTIVE). An unready company cannot fuel anyway: it has no tag and no balance. A computed readiness checklist gives Ops the same control without touching `status`, the RLS, the snapshot and every reader of company status.
- **Partitioning.** Not needed at the year-one volume (section 7).
- **Kafka, RabbitMQ, Redis** for import or jobs. PostgreSQL with `FOR UPDATE SKIP LOCKED` is enough.
- **Serializable isolation.** The row-lock design already gives the invariants at read committed.

---

## 4. Findings in detail

| ID | Area | Evidence | Risk | Proposed change |
|---|---|---|---|---|
| F1 | Top-up | `TopUps.confirm` posts `topUp.amountPiastres()` | Wallet credited with money that never arrived, or with less than arrived | `confirm` requires `receivedAmountPiastres` (> 0, ≤ cap). Credit that amount. Store `amount_received_piastres` and `evidence_reference` (new columns). Keep `amount_piastres` as requested. Audit both when they differ. API: new required field, which is a contract change for the Ops screen. Migration: nullable columns, existing rows backfilled with `amount_received = amount` for `CONFIRMED`. CHECK `(status = 'CONFIRMED') = (amount_received_piastres IS NOT NULL)`. |
| F2 | Direct credit | `TopUps.creditDirectly` | Single-person money creation | Above `zawed.finance.direct-credit-approval-threshold-piastres` (default 5,000,000), a direct credit is recorded `PENDING_APPROVAL` and a different user approves. Reuse the topup row and the existing two-person CHECK pattern from V9. |
| F3 | Company create | `Companies.create`; no unique besides `tax_id` | Duplicate tenants on retry | `Idempotency-Key` header (stored with a request hash: same key and same body replays; same key and another body gives `409 IDEMPOTENCY_MISMATCH`). Small `idempotency_keys` table scoped by actor. Reusable for other Ops creates. |
| F4 | Bulk import | absent | Onboarding cost | Section 5.4 |
| F5 | Nonces | `device_nonces`, comment in b4 contract §1 | Unbounded growth, slower inserts | Daily purge of nonces older than 1 hour (≥ 2× the 5-minute window), batched deletes, with a one-runner guard |
| F6 | Fueling reads | indexes `fuelings (company_id, id)`, `(station_id, id)`, `(operator_id, id)`; filters on `evaluated_at` | Range scans over the whole tenant history | Measure first on a synthetic 10–25 M-row dataset. Expected fix: `(company_id, evaluated_at)`, `(operator_id, evaluated_at)` and a BRIN on `evaluated_at` for platform-wide day aggregates, built `CONCURRENTLY`. |
| F7 | Offline | `zawed.sync.offline-exposure-cap-piastres` per device and company; snapshot allowance ≤ 5,000 EGP per device | Exposure = devices × allowance | Section 6 |
| H1 | Tag/vehicle identity columns | grants in `V4:243` | Silent history rewrite | Immutable-column trigger |
| H2 | Idempotency binding | `FuelingEngine.java:624-626` | Wrong replay | Compare fuel type / `deviceAt` |
| H3 | Sync batch duplicates | no unique index | Monitor double counts | Unique index plus `ON CONFLICT DO NOTHING` |
| H4 | Finance retries | only the fueling engine retries | 500 on a rare deadlock | Shared retry-once |
| H5 | Tank bound | V4 CHECK | Typos | `NOT VALID` check, then `VALIDATE` |
| H6 | Plate key | `PlateKey.java` | Missed duplicates | Canonical digit/letter grouping and collision report |
| H7 | Scale-out | in-memory limiter, `@Scheduled` | Only once there are more than one instance | Advisory-lock guard now (cheap); shared limiter later |

---

## 5. Onboarding design (proposed)

### 5.1 Company

Keep the current `POST /ops/companies` (company plus wallet in one transaction) and add:
- `Idempotency-Key` (F3);
- `GET /ops/companies/{id}/onboarding`: a computed checklist, with no stored state:
  - legal and contact info present;
  - at least one department (optional);
  - cooldown and low-balance threshold set;
  - vehicles count, and how many have an active tag;
  - active limit rules;
  - wallet balance > 0 (yes/no);
  - at least one company user.

The Ops company page shows the checklist with links to each step. The company `status` (`ACTIVE`, `FROZEN`) is unchanged.

### 5.2 Vehicle registration

Existing fields cover the requirement. Add:
- `fleet_code`: optional, unique per company among non-decommissioned vehicles;
- the tank upper bound (H5);
- the canonical plate key (H6).

Plate uniqueness stays **global among non-decommissioned vehicles**: a physical plate is one vehicle. A vehicle moving between companies is decommissioned at the old one first. That is a manual Ops step today, and could become an explicit "transfer" action later.

Lifecycle stays:
- ACTIVE ⇄ SUSPENDED;
- ACTIVE or SUSPENDED → DECOMMISSIONED, which is terminal.

### 5.3 RFID

Keep the current model: one row per tag, UID unique for ever, at most one `ACTIVE` per vehicle, and the explicit transition table. Add the immutable-column trigger (H1). Concurrent issue of two tags to one vehicle is already serialised by the vehicle lock and the partial unique index. A test will prove it.

### 5.4 Bulk vehicle import

**Who:** Ops only, for one company per file (company portals stay read-only).

**Format:** CSV (UTF-8, Excel-friendly) in v1. XLSX needs a parsing library: I'd add the small streaming reader `org.dhatim:fastexcel-reader` rather than Apache POI. That is your call (decision D4).

**Limits:** 5,000 rows and 5 MB per file. Larger files are refused with a clear message, never truncated.

**Columns:** `plate` (required), `fleet_code`, `make`, `model`, `year`, `fuel_type` (accepts English codes and Arabic names), `tank_capacity_l`, `department` (matched by name within the company), `tier`, and optional daily, weekly and monthly litre or amount limits. Tags are **not** imported: tags are provisioned by installers at the vehicle.

**Tables (justified by preview, progress, retry and audit):**
- `fleet_import_jobs`:
  - id, company_id, file name, file sha256, row count;
  - status `VALIDATED | CONFIRMED | RUNNING | COMPLETED | COMPLETED_WITH_ERRORS | CANCELLED`;
  - mode `STRICT | VALID_ROWS_ONLY`;
  - counts; created_by, confirmed_by; lease_owner, lease_until; timestamps.
- `fleet_import_rows`:
  - job_id, row_number;
  - normalised values (jsonb), plate_key;
  - status `VALID | INVALID | DUPLICATE_IN_FILE | EXISTS | IMPORTED | FAILED`;
  - errors (jsonb), vehicle_id.

**Flow:**
1. **Upload and validate**, in one short request with no locks. The file is parsed with bounded memory, every row is validated, and in-file duplicates and existing plates are found with one query. Rows are stored. The answer is a preview: counts, plus errors by row and column.
2. **Confirm** (a separate request, by any Ops user with `FLEET_MANAGE`). In `STRICT` mode (the default) it is refused while any row is invalid. `VALID_ROWS_ONLY` must be chosen explicitly.
3. **Execute asynchronously** in a PostgreSQL-backed worker:
   - it claims the job with `FOR UPDATE SKIP LOCKED` and a lease;
   - it inserts rows in batches of 200, each batch its own short transaction;
   - each row is marked `IMPORTED` with its `vehicle_id` in the same transaction as the insert;
   - the plate unique index is the final defence, and a plate taken meanwhile marks the row `EXISTS`.
4. **Recovery:** after a crash or restart, an expired lease lets the next poll resume at the first row not yet `IMPORTED`. Nothing is inserted twice.
5. **Result** screen with downloadable row errors. Exported CSV values are defused against formula injection (the existing `FuelingExports` pattern).

**Defaults and trade-offs:** strict validation plus batched execution. True all-or-nothing for 5,000 rows in one transaction is possible (a few seconds), but holds locks on the plate index for the whole run and gives no progress. Batched execution with per-row state is resumable and idempotent. Rows are independent, so partial completion after a confirmed strict validation is only possible through a race (a plate registered meanwhile), and is reported row by row.

**Scope:** the worker runs in the platform scope as the confirming Ops user (recorded on the job), through a new, reviewed entry in the `ArchitectureTests` escalation list. Every insert still passes the same-tenant trigger and the job's `company_id`.

**Audit:** `FLEET_IMPORT_VALIDATED`, `FLEET_IMPORT_CONFIRMED`, `FLEET_IMPORT_COMPLETED`, plus one `VEHICLE_CREATED` per row with `importJobId`.

---

## 6. Offline exposure

**Today:**
- the snapshot allowance is `min(balance, 5,000 EGP)` per device;
- the server holds the payables past 10,000 EGP of offline fuel per device and company in 24 hours.

With *n* devices offline at once, a company's worst case is about *n* × 5,000 EGP decided against a stale balance. The server records it all, and the station is paid. The shortfall becomes the company's receivable, and payouts on held payables wait for Ops. Nothing is lost silently, but the debt is not bounded per company.

**Options:**
1. **Keep it as it is** and alert on the receivable. That is correct accounting with unbounded credit risk.
2. **A per-company receivable ceiling** (recommended, small).
   - When a company's receivable exceeds `X`, new snapshots show its wallet as `SUSPENDED` (offline refused), and online fueling continues as normal.
   - This bounds the total debt to about *X* + the last snapshot window, with no connectivity requirement.
3. **Split the allowance across a company's recently active devices.** That is more precise but also more complex, and fragile with stale device counts.

Recommend option 2. The threshold is an owner decision (D3).

---

## 7. Performance and capacity

### 7.1 Workload model

These are estimates, not measurements:

| Input | Assumption |
|---|---|
| Vehicles | 200,000 |
| Fuelings per vehicle | one every 3 days → about 67,000 a day |
| Peak hour | 15 % of the day → about 10,000 an hour ≈ 2.8/s average, bursts of about 15/s |
| Writes per fueling | about 8 rows (fueling, ledger transaction, 2–3 entries, wallet, payable, tag, sometimes an alert) |
| Growth | about 24 M fuelings and 70 M ledger entries a year |
| Hot rows | One wallet per company: a 2,000-vehicle company at peak is about 0.3 fuelings/s against a lock held for a few ms. One payable per station: about 1 a minute at a busy station. |

**Conclusion (to verify):** the lock design is far from contention at this scale. The likely first limits are read paths over history (F6), retention (F5) and the connection pool: the default Hikari pool is 10, which needs sizing against the measured transaction time.

### 7.2 To measure in Phase E

- `EXPLAIN (ANALYZE, BUFFERS)` on the portal lists, exports, calendar, statement and reconciliation, against a synthetic dataset (100 companies, 200,000 vehicles, 10–25 M fuelings), generated with SQL `generate_series` on the embedded PostgreSQL.
- Reconciliation recomputes every account from all its entries each night. That is fine at year one; an incremental checkpoint is a later option.
- **Pagination:** lists page by uuid v7 `id`, made by the application before the insert, so the order is "creation order", not commit order. A row created earlier but committed after a client fetched past it can be missed by that client's next page.
  - The statement pages by `seq`, also assigned at insert, but it is safe per station because every payable posting holds the payable lock.
  - For lists this is an accepted edge (newest-first lists). Exports use time ranges and are complete.

### 7.3 Partitioning

Not now. Reconsider for `fuelings` and `ledger_entries` past about 100 M rows, or when retention requires dropping old periods.

---

## 8. Concurrency risk matrix

| Operation | Shared state | Invariant | Isolation and locks (order) | Idempotency | Retry | Status |
|---|---|---|---|---|---|---|
| Online fueling | wallet, vehicle, tag, payable | balance ≥ 0; limits; cooldown; one fueling per id | Read committed; wallet `FOR UPDATE` → vehicle `FOR SHARE` → tag → payable (inside the posting) | `client_txn_id` unique, payload-bound (tag and volume) | Deadlock retried once | ✅ (H2: bind fuel type) |
| Two fuelings, same vehicle or tag | same | cooldown and limits | Serialised by the wallet lock | — | — | ✅ needs explicit test |
| Two fuelings, two vehicles, one wallet | wallet | balance ≥ 0 | Wallet lock serialises the company only | — | — | ✅ needs explicit test |
| Fueling during top-up | wallet | — | Both take the wallet lock | Top-up: reference key plus source key | — | ✅ needs test |
| Fueling during suspension or limit change | vehicle, limits | decision sees committed state | Status and limit writers lock the vehicle `FOR UPDATE`; fueling holds it `FOR SHARE` | — | — | ✅ needs test |
| Top-up confirm ×2 | request, wallet | credited once | request `FOR UPDATE` → wallet; conditional flip | reference key unique; source unique | none | ✅ tested (F1 aside) |
| Direct credit | wallet | credited once per reference | wallet → insert | reference key unique | none | ⚠️ F2 |
| Payout ×2 | payable | ≤ available − pending | payout row → payable `FOR UPDATE` | reference key unique; source unique | none (H4) | ✅ needs test |
| Payout during reversal | payable (and wallet) | available never negative-paid | reversal: wallet → payable; payout: payable. No cycle | one reversal per transaction | none (H4) | ✅ needs test |
| Reversal approve ×2 | reversal, wallet, payable | once | reversal row lock; conditional flip; `ledger_transactions_reversed_once` | unique live reversal | none | ✅ |
| Tag issue ×2 | vehicle, tags | ≤ 1 active per vehicle; UID unique | vehicle `FOR UPDATE`; partial unique index | UID unique | none | ✅ needs test |
| Vehicle create ×2 (same plate) | plate index | one active plate | unique index | plate key | none | ✅ (H6 key quality) |
| Offline sync batch ×2 | fuelings, wallet, payable | each capture once | per-item transaction; same engine locks | `client_txn_id`; derived id for declined-online | per-item `RETRY` | ✅ (H3 monitor) |
| Agent sessions | session row | one live per agent | partial unique `agent_sessions_one_live_per_agent` | — | — | ✅ |
| Import jobs | job, plate index | each row once | job `FOR UPDATE SKIP LOCKED` plus lease; batch transactions | row state plus plate unique | resume from lease | proposed |
| Company create | — | one company per real entity | none | none | — | ⚠️ F3 |

---

## 9. Proposed change set (smallest safe set)

### P0: must fix before production

1. **F1 received amount and evidence on top-up confirmation.** Migration V12; API field (Ops screen updated); tests.
2. **F2 two-person direct credit above a threshold.** Migration V12; tests.
3. **F3 `Idempotency-Key` on company creation.** Migration V13 (`idempotency_keys`); tests.
4. **F5 nonce purge job**, with a one-runner advisory-lock guard, also applied to reconciliation (H7).
5. **H1 immutable identity columns** on `rfid_tags` and `vehicles` (trigger).
6. **H2 idempotency binding, H3 sync batch uniqueness, H4 retry-once on finance commands.**
7. **Concurrency test suite** for every "needs test" row of section 8, asserting balances and row counts through separate connections (`support/Concurrently`).

### P1: fleet onboarding

8. **Onboarding readiness endpoint and Ops checklist UI.**
9. **`fleet_code`, tank upper bound, canonical plate key** (with a collision report on existing data).
10. **Bulk vehicle import:** V14 tables, API, worker, Ops screens (template, upload, preview, confirm, progress, results), tests including crash recovery and overlapping imports.
11. **Offline per-company receivable ceiling** (option 2), if approved.

### P2: performance

12. **Synthetic dataset generator, `EXPLAIN` report, indexes for F6** (built `CONCURRENTLY`, in a migration Flyway runs outside a transaction).
13. **k6 scripts** (fueling mix, top-up, import, reads), run locally against the embedded PostgreSQL. Results reported as *local machine* numbers, labelled as such.
14. **Connection pool sizing** from the measurements.

### P3: later, optional

- Shared rate limiter for multi-instance.
- Incremental reconciliation.
- XLSX import (if not in v1).
- Vehicle transfer between companies.
- Partitioning.

### Documentation

Each change updates:
- `database-erd.md` (English) and the Arabic page;
- a new `docs/architecture/database-changelog.md`;
- ADRs:
  - 0021 top-up verification and direct-credit control;
  - 0022 idempotency keys;
  - 0023 bulk import strategy;
  - 0024 vehicle identity, plate key and fleet code;
  - 0025 offline exposure ceiling, if approved.

---

## 10. Decisions needed

| # | Question | Safe default if you agree |
|---|---|---|
| D1 | Direct-credit approval threshold | 50,000 EGP (same as payouts), in config |
| D2 | Is commercial registration unique per company? | Yes, normalised, partial unique |
| D3 | Per-company offline receivable ceiling | 20,000 EGP, in config; above it, offline is refused for that company |
| D4 | Import format for v1 | CSV now; XLSX with `fastexcel-reader` as a follow-up |
| D5 | Plate identity: letters + digits only, or + plate category? | Letters + digits (current), with a research note |
| D6 | Who may confirm an import | Any Ops user with `FLEET_MANAGE` (no second person; vehicles move no money) |

## 11. Verification plan

- **New tests per change:** constraint tests, RLS tests under `zawed_app`, and concurrency tests with barriers and separate connections, asserting balances, counts and ledger sums.
- **The full suite** (`mvnw verify`, currently 283 tests) and the web checks plus E2E on every phase.
- **Load and recovery tests** run locally and are labelled as such, or marked **NOT EXECUTED** where the environment is missing.
