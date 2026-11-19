# Performance evidence: read paths on `fuelings`, indexes, load

**Date:** 2026-11-19 · **Branch:** `feature/perf-evidence` · **Audit items:** F6, §7.2 of
`docs/architecture/production-readiness-audit.md`

Every number in this report was measured by the two test classes named below, on the machine
described in §1. Nothing here was measured on production hardware, and nothing was run with k6
(§8). The raw outputs are in [`perf-evidence/`](perf-evidence/).

## Summary

- **Dataset at the full target size:** 100 companies, 200,000 vehicles with one active tag each,
  20 operators, 300 stations, 1,000 devices, 2,000 agents, **5,000,000 fuelings** over 12 months
  (4.05 GB with indexes), plus 50,007 alerts. No scaling down was needed.
- **The engine's own reads are already fast** (vehicle usage, cooldown look-up, offline exposure):
  under 1.1 ms in every run, on the existing V7/V8_1 indexes.
- **Calendars, exports and the POS snapshot usage were slow** at 5 M rows (0.6–4.2 s) and are fixed
  by **three indexes in V16**: `(company_id, evaluated_at)`, `(operator_id, evaluated_at)` and a
  BRIN on `evaluated_at`. For example, the largest company's month of calendar went from 4.18 s to
  0.16 s and its export count from 0.63 s to 0.12 s.
- **Lists with an old `from`/`to` range stay slow (about 1.6 s) whatever the index.** The list
  orders by `id` and PostgreSQL walks the primary key backwards through all newer rows. This needs a
  query change, not an index: ordering by `evaluated_at desc, id desc` took the same company and
  operator lists from about 2.5 s to under 2 ms on the V16 indexes (measured, §4). Not changed here:
  main code was out of scope.
- **`CREATE INDEX CONCURRENTLY` hangs under the current Flyway configuration** (Flyway's
  transactional lock holds a snapshot that the concurrent build waits for). V16 therefore uses plain
  `CREATE INDEX`; the concurrent route needs one configuration line, shown working (§6).
- **Concurrent online fuelings through the HTTP API** (20 threads × 200, one shared hot wallet):
  340.6 fuelings/s, p95 124.5 ms overall, p95 172.8 ms on the hot wallet, 0 errors, 0 deadlocks,
  every wallet and payable reconciled to the piastre (§7).

## 1. Environment

| Item | Value |
|---|---|
| Machine | Developer laptop: AMD Ryzen 5 4600H (6 cores, 12 threads), 15.4 GB RAM |
| OS | Windows 11 Home 10.0.26300 |
| Disks | Query dataset on D: (Samsung SSD 860 EVO, SATA); the test suite's database on C: (Samsung NVMe) |
| JVM | OpenJDK 64-Bit Server VM 25.0.2+10-LTS (Temurin) |
| PostgreSQL | 18.6 on x86_64-windows (MSVC), embedded (Zonky binaries 18.6.0), started by the test |
| Server settings (query harness) | `shared_buffers=512MB`, `maintenance_work_mem=256MB`, `max_wal_size=4GB`, `checkpoint_timeout=15min`; defaults otherwise: `work_mem=4MB`, `effective_cache_size=4GB`, `random_page_cost=4`, `max_parallel_workers_per_gather=2`, `jit=on`. Zonky sets `fsync=off` and `synchronous_commit=off`, which flatters every write time |
| Load measurement | The test suite's embedded PostgreSQL with its defaults; client, API and database on the same laptop |

**This is embedded PostgreSQL on a developer laptop, not production hardware.** Absolute times will
differ on the production database; the plan shapes and the before/after ratios are the evidence.

Three harness runs were made on the same data directory:

| Run | When (UTC) | Phases | Machine state |
|---|---|---|---|
| 1 | 00:14–00:40 | load, `baseline`, `brin`, `time`, `composite`, `composite_brin`, `company_operator_brin` | No other workload observed. **The before/after comparison below uses this run.** |
| 2 | 06:00–06:11 | `baseline`, then `v16` with `CREATE INDEX CONCURRENTLY` | Stopped: the concurrent build hung (§6). Its baseline is not used |
| 3 | 06:14–06:29 | `baseline`, `v16`, `v16_concurrent` | **Busy:** total CPU at 95–99 % from another engineer's builds (8 other `java` and 29 `postgres` processes); times are 1.3–11× those of run 1 for the same plan. Used for the V16 application and the list rewrite, compared only within the run |

## 2. Dataset and method

`apps/api/src/test/java/com/zawed/api/perf/QueryPlanReport.java`, run with

```
mvnw test -Dtest=QueryPlanReport -Dzawed.perf=true -Dzawed.perf.dataDir=D:/zawed-perf/pgdata
```

It is not part of the normal suite: its name matches no Surefire pattern and it needs
`-Dzawed.perf=true` (`@EnabledIfSystemProperty`). It starts its own embedded PostgreSQL with the
data directory on D:, because C: had 1.6 GB free and the dataset takes over 4 GB. It applies the
Flyway migrations V1–V11 as the owner.

| Table | Rows | Heap + indexes |
|---|---:|---:|
| companies | 100 | 88 kB |
| vehicles | 200,000 | 66 MB |
| rfid_tags | 200,000 (one active per vehicle) | 70 MB |
| station_operators | 20 | 40 kB |
| stations | 300 | 168 kB |
| pos_devices | 1,000 | 392 kB |
| agents | 2,000 | 712 kB |
| fuelings | 5,000,000 | 4,052 MB (heap 1,890 MB) |
| alerts | 50,007 | 17 MB |

- **Generated with set-based SQL** (`generate_series`, seeded `random()`), in 20 chunks of 250,000,
  in **748 s** in total.
- **Shape:**
  - Fuelings are spread evenly over the 365 days before the load, in receipt order.
  - The id is a UUIDv7 of the receipt time, as the application makes it.
  - 95 % completed, 5 % declined. 10 % of completed fuelings are offline, evaluated up to 24 h before
    receipt, so `evaluated_at` is not perfectly in physical order. 20 % of offline ones carry a conflict.
  - Company sizes are skewed (u² slotting). The largest company has 19,869 vehicles and 496,504
    fuelings; the median one has 1,404 vehicles and 35,365. Each operator has about 250,000 fuelings,
    each station about 17,000.
- **Constraints:**
  - Every foreign key and CHECK constraint was enforced during the load. Ledger rows were left out
    (optional in the brief), so synthetic fuelings are `billable = false` with no ledger transaction,
    which the CHECK allows. The measured reads do not read `billable` or the ledger.
  - The `fuelings_same_tenant` trigger was **disabled for the bulk insert only**
    (`ALTER TABLE fuelings DISABLE TRIGGER fuelings_same_tenant`, as owner), then re-enabled.
  - The same rule was then checked for the whole table in one query: 0 violations, asserted.
  - The immutability triggers fire only on UPDATE, DELETE and TRUNCATE, and were left on.
- **After the load:** `VACUUM (ANALYZE)` on fuelings and alerts, then `ANALYZE` on the database.
- **Queries:** the SQL is copied verbatim from:
  - `Fuelings.list`;
  - `Activity.days` (both statements);
  - `FuelingExports.csv` (count and rows);
  - `FuelingUsageReader.usageSince`;
  - `Fuelings.volumeIn`, `latestCompletedBetween` and `offlineAmountSince`.

  Named parameters become `$n`.
- **How each query runs:**
  - On a connection that ran `SET ROLE zawed_app`.
  - In a transaction that first runs the exact `set_config('app.scope_type' | 'app.scope_id' |
    'app.user_id', …, true)` statement of `ScopedTransactionManager` and checks `current_user`. The
    RLS policy is therefore in every plan (visible as a filter in the plan files).
  - `PREPARE` with the parameter types, then `EXECUTE` as a custom plan
    (`plan_cache_mode=force_custom_plan`).
  - Sequence: one first execution, `EXPLAIN (ANALYZE, BUFFERS, SETTINGS)`, then 5 timed executions
    with all rows read by the client. The tables report the median of the 5.
- **Write cost:**
  - 2,000 single-row inserts in one transaction, and one 20,000-row `INSERT … SELECT`, of fresh rows
    at random companies and stations.
  - The same-tenant trigger is on. Each transaction is rolled back. Median of 3.
- **Windows** (Africa/Cairo):
  - "old month" is 2026-05;
  - "last month" is 2026-10;
  - "last 30 days" ends at the dataset end, 2026-11-19 00:14:58 UTC;
  - the snapshot periods are the day, week (from Saturday) and month containing the dataset end.

## 3. Results: run 1, before and after

Median milliseconds. **Baseline** = V1–V11 indexes. **Chosen** = the V16 set
(`company_operator_brin`). The other columns are the candidates tried.

| Query (scope) | baseline | brin | time (B-tree) | composite (company, operator, station) | composite + brin | **chosen** |
|---|---:|---:|---:|---:|---:|---:|
| Ops list, newest (platform) | 4.07 | 1.42 | 0.76 | 0.83 | 0.93 | **0.92** |
| Ops list, last 30 days | 1.58 | 1.03 | 0.91 | 0.96 | 0.87 | **0.96** |
| Ops list, old month | 1,611 | 1,624 | 1,608 | 1,602 | 1,620 | **1,594** |
| Ops list by company, old month | 1,619 | 1,648 | 1,619 | 1,619 | 1,618 | **1,621** |
| Company list, newest (largest) | 1.50 | 1.50 | 1.32 | 1.43 | 1.41 | **1.05** |
| Company list, last 30 days (largest) | 1.52 | 1.63 | 1.40 | 1.59 | 1.34 | **1.58** |
| Company list, last month (largest) | 65.5 | 64.3 | 60.9 | 62.8 | 66.1 | **63.6** |
| Company list, old month (largest) | 1,620 | 1,618 | 1,604 | 1,615 | 1,615 | **1,638** |
| Company list, old month (median) | 14.0 | 15.1 | 16.0 | 13.0 | 14.5 | **14.6** |
| Station list, newest (operator) | 1.32 | 1.21 | 1.06 | 1.33 | 1.10 | **0.93** |
| Station list, old month (operator) | 63.1 | 15.2 | 25.8 | 12.3 | 12.7 | **12.3** |
| Operator list, newest | 1.67 | 1.28 | 1.51 | 1.12 | 1.17 | **1.52** |
| Operator list, old month | 1,620 | 1,622 | 1,614 | 1,608 | 1,621 | **1,610** |
| Activity, month, largest company | 4,180 | 305 | 153 | 160 | 163 | **161** |
| Activity, month, median company | 71.3 | 32.2 | 40.2 | 13.6 | 14.4 | **14.1** |
| Activity, month, operator | 748 | 189 | 149 | 88.5 | 91.7 | **94.1** |
| Activity, month, platform | 1,039 | 550 | 567 | 4,677 | 542 | **538** |
| Activity alerts, month, company | 4.88 | 3.56 | 4.69 | 3.38 | 4.61 | **4.67** |
| Activity alerts, month, platform | 14.3 | 13.6 | 10.8 | 11.7 | 13.5 | **12.9** |
| Export count, last month, largest company | 630 | 252 | 160 | 110 | 110 | **118** |
| Export rows, last month, largest company (41,942 rows) | 842 | 478 | 341 | 294 | 315 | **305** |
| Export count, last month, operator | 749 | 161 | 112 | 67.4 | 65.3 | **68.6** |
| Export rows, last month, operator (21,271 rows) | 829 | 282 | 238 | 186 | 177 | **188** |
| POS snapshot usage since period starts (138,316 vehicles) | 1,159 | 634 | 610 | 605 | 606 | **628** |
| Engine: vehicle volume in month | 0.63 | 0.31 | 0.42 | 0.46 | 0.34 | **0.41** |
| Engine: latest completed between | 0.60 | 0.53 | 0.52 | 0.63 | 0.43 | **0.46** |
| Engine: offline amount since 24 h | 0.66 | 0.34 | 0.33 | 0.36 | 0.46 | **0.34** |

### How the plans changed

| Query | Baseline plan (run 1) | With V16 |
|---|---|---|
| Activity, largest company | Parallel Seq Scan of the whole table (each of 3 processes removes about 1.65 M rows) | Bitmap Index Scan on `(company_id, evaluated_at)`: only the month |
| Activity, platform month | Parallel Seq Scan of the whole table (each of 3 processes removes about 1.53 M rows), sort spilled to disk | Bitmap Index Scan on the BRIN: only the month's blocks |
| Exports (company, operator) | Company: Parallel Seq Scan of the whole table. Operator: bitmap scan of the operator's whole history on `fuelings_operator_id_idx` | Bitmap Index Scan on the matching composite index |
| Snapshot usage | Parallel Seq Scan of the whole table | Bitmap Index Scan on the BRIN |
| Station list, old month | Bitmap scans of the station's and the operator's whole history, then top-N sort | Bitmap scans of `(operator_id, evaluated_at)` (only the month) and `station_id`, then top-N sort |
| Lists with an old range | `Index Scan Backward using fuelings_pkey`, **2.33–2.34 M rows removed by the filter** before 51 match | Unchanged: the planner keeps the same plan with every candidate |
| Engine reads | `fuelings_vehicle_usage_idx`, `fuelings_offline_exposure_idx` | Unchanged |

The `composite` phase without the BRIN made the platform calendar slower (1,039 ms to 4,677 ms):
the planner chose another plan. The BRIN removes that risk, which is one more reason it is in V16.

Plan files: [`perf-evidence/plans-run1/`](perf-evidence/plans-run1/) (candidates) and
[`perf-evidence/plans-run3/`](perf-evidence/plans-run3/) (baseline, V16, concurrent V16). Run 1's own
baseline plan files were overwritten by run 2. Its plan shapes are in the "Plan" column of
[`run1-summary.md`](perf-evidence/run1-summary.md).

## 4. Lists with a date range: an index does not fix them

`Fuelings.list` filters `evaluated_at` and orders by `id desc limit 51`. The planner estimates the
matching rows as spread evenly through the primary key. In reality they sit months back in it, so it
reads the primary key backwards from the newest row and throws away 2.3 M rows first. The plan is
the same with every candidate index (§3), at about 1.6 s in run 1 for the Ops, company and operator
lists of a month six months back. For the last month, the largest company's list reads 246,477 rows
first (64 ms).

The harness also measured a **candidate rewrite** (not the API's SQL). It uses the same filters but
`order by evaluated_at desc, id desc` (rows `proposal_*` in
[`run3-summary.md`](perf-evidence/run3-summary.md)). Run 3 timings are on the busy machine:

| List, old month | API SQL, V16 | Rewrite, baseline indexes | Rewrite, V16 |
|---|---:|---:|---:|
| Ops, platform-wide | 2,549 ms | 1,430 ms | 373 ms (BRIN + top-N sort) |
| Ops, by company | 2,547 ms | 1,095 ms | 1.62 ms |
| Company (largest) | 2,533 ms | 1,243 ms | 1.81 ms |
| Operator | 2,536 ms | 1,201 ms | 1.76 ms |

**Recommendation (not done here):**
- When `from` or `to` is given, order and page by `(evaluated_at, id)`, with a cursor holding both.
- Keep `id` paging for the unfiltered "newest" list, which is already about 1 ms.

This is a contract change for the list cursor, so it needs its own phase.

## 5. Indexes added (V16) and their write cost

`apps/api/src/main/resources/db/migration/V16__fuelings_time_indexes.sql`:

| Index | Size at 5 M rows | Build (run 1, plain) | Why |
|---|---:|---:|---|
| `fuelings_company_time_idx (company_id, evaluated_at)` | 194 MB | 8.5 s | Company calendar 4,180 → 161 ms (largest), 71 → 14 ms (median); company export count 630 → 118 ms, rows 842 → 305 ms |
| `fuelings_operator_time_idx (operator_id, evaluated_at)` | 194 MB | 10.7 s | Operator calendar 748 → 94 ms; operator export count 749 → 69 ms, rows 829 → 188 ms; station list for an old month 63 → 12 ms |
| `fuelings_evaluated_at_brin USING brin (evaluated_at)` | 80 kB | 5.9 s | Platform calendar 1,039 → 538 ms; POS snapshot usage 1,159 → 628 ms; avoids the 4.7 s plan seen without it |

**Not added, because the plans did not justify them:**
- `(station_id, evaluated_at)`, 194 MB: no measured query got faster with it once
  `(operator_id, evaluated_at)` existed. Station list for an old month: 12.33 ms in both phases.
- A B-tree on `evaluated_at`, 107 MB: it gave the same platform-wide gains as the 80 kB BRIN.

**Write cost** (run 1, baseline → V16 set; median of 3, rolled back, same-tenant trigger on):

| Measure | Baseline | V16 set | Change |
|---|---:|---:|---:|
| Single-row insert, one transaction of 2,000 | 0.497 ms/row | 0.536 ms/row | +0.04 ms (+8 %) |
| Bulk insert of 20,000 rows | 3,061 ms | 3,720 ms | +659 ms (+22 %) |
| Disk at 5 M rows | | +388 MB | +10 % of the table with its indexes (4,052 MB) |

The single-row figure is the one that matters for the POS path (one fueling = one row). The B-tree
on all three composite columns cost more: 0.580 ms/row, 3,698 ms bulk. Write timings are noisy (run 3
on the busy machine measured 1.11 ms/row before and 1.14 ms/row after) and use `fsync=off`.

## 6. `CREATE INDEX CONCURRENTLY` and Flyway

The brief asked for `CREATE INDEX CONCURRENTLY IF NOT EXISTS` with a
`V16__fuelings_time_indexes.sql.conf` (`executeInTransaction=false`). Flyway 12.4 honoured the
`.conf` (it logged `[non-transactional]`), but in run 2 the first index build **never finished**:

- `pg_stat_progress_create_index` showed phase "waiting for old snapshots" on
  `fuelings_company_time_idx`. The build session waited on `virtualxid` for over 5 minutes.
- `pg_blocking_pids` named the blocker: Flyway's own second connection, `idle in transaction`,
  holding `backend_xmin` (its last statement was `SELECT COUNT(*) FROM pg_namespace …`). That is
  Flyway's PostgreSQL transactional advisory lock, on by default. A concurrent build waits for every
  older snapshot, including the snapshot of the session that waits for the migration. That is a
  deadlock nothing detects. The build was cancelled with `pg_terminate_backend`.
- The wait is on Flyway's session, not on the table, so the same is expected on an empty table and in
  the test suite. This was not run, because it would not finish.

What was committed: **V16 with plain `CREATE INDEX IF NOT EXISTS`**, in Flyway's normal transaction
(no `.conf`):
- All or nothing.
- Inserts into `fuelings` wait while the indexes build. On 5 M rows that took 8.5 s, 10.7 s and
  5.9 s as separate builds in run 1, and **30.5 s** for the whole script through Flyway in run 3
  (busy machine).

The concurrent route works with one more setting, measured in run 3 (`v16_concurrent`):
- Flyway's session-level lock (`postgresql.transactional.lock=false`).
- The `.conf` and `CONCURRENTLY`.
- Result: applied in **56.9 s** on 5 M rows, all three indexes valid, the same plans as V16.

To switch, in a change that may touch main configuration:
1. Add `spring.flyway.postgresql.transactional-lock: false` to `application.yaml`.
2. Add `CONCURRENTLY` to V16's three statements.
3. Add the `.conf` file.

If a concurrent build ever fails, it leaves an INVALID index that `IF NOT EXISTS` then skips. Drop it
(`DROP INDEX CONCURRENTLY`) and run `flyway repair` before migrating again.

**Caveat on the run 3 `v16` figure:** a stale `V16__fuelings_time_indexes.sql.conf` from run 2 was
still in `target/classes`, because the build was not cleaned between runs. So that application ran
non-transactionally. The statements and their cost are the same; in a transaction the inserts wait
for the whole 30.5 s instead of for each index in turn. The full suite was then run after
`mvnw clean` (§9).

**Merge order:** V16 does not depend on V12–V15 (it only adds indexes to a V7 table). Flyway refuses
by default to apply V12–V15 on a database that already has V16. So V16 should reach `develop` after
V12–V15, or `outOfOrder` would be needed.

## 7. Load measurement inside the test JVM (executed)

`apps/api/src/test/java/com/zawed/api/fueling/OnlineFuelingLoadMeasurement.java`, run with
`mvnw test -Dtest=OnlineFuelingLoadMeasurement -Dzawed.perf=true` (also excluded from the normal
suite). It drives the real HTTP API on the suite's embedded PostgreSQL. Run 2026-11-19 06:33 UTC,
**with the machine at 95–97 % CPU from other work before it started**. Output:
[`perf-evidence/load-measurement.md`](perf-evidence/load-measurement.md).

**Setup, all through the API:**
- 5 operators and 10 stations.
- 40 devices. Each was activated with its own P-256 key, got a signed device token, had an agent
  sign in, and had the PIN changed.
- 100 "spread" companies, 1 "hot" company and 1 warm-up company.
- 4,200 vehicles with tags. Each fueling uses its own vehicle, because of the cooldown.
- 8,400 setup calls, 54.9 s.

| Setup call, 8 concurrent callers | n | p50 | p95 | p99 | max |
|---|---:|---:|---:|---:|---:|
| `POST /ops/vehicles` | 4,200 | 8.1 ms | 17.6 ms | 27.8 ms | 118.2 ms |
| `POST /ops/vehicles/{id}/tags` | 4,200 | 9.3 ms | 19.0 ms | 31.0 ms | 148.4 ms |

**Online fuelings** (`POST /pos/fuelings` with device token and agent session):
- 20 threads × 200 = 4,000 requests, after a warm-up of 200.
- Every fourth fueling of each thread goes to the hot company, so 1,000 fuelings contend for one
  wallet lock. The other 3,000 go to 100 companies.
- Closed loop: each thread sends its next request as soon as the last one answers.
- HikariCP pool: 10, the default.

| Measure | Result |
|---|---|
| Wall time, throughput | 11.74 s, **340.6 fuelings/s** |
| Latency, all | p50 48.0 ms, **p95 124.5 ms**, p99 181.7 ms, max 490.0 ms |
| Latency, hot wallet (1,000) | p50 83.8 ms, **p95 172.8 ms**, p99 234.6 ms, max 490.0 ms |
| Latency, spread companies (3,000) | p50 43.5 ms, p95 67.9 ms, p99 84.5 ms, max 143.7 ms |
| Outcomes | 4,000 COMPLETED, 0 server errors, 0 other |
| Deadlocks (`pg_stat_database.deadlocks` delta) | **0** |
| Wallets (102) | balance = credited − charged = sum of ledger entries = last `balance_after`: **all equal** |
| Station payables (10) | cached 126,000,000 = from entries 126,000,000 = total charged 126,000,000 piastres (4,200 × 30,000) |
| Unbalanced fueling transactions | 0 |

**What this does and does not show:**
- It shows that the wallet-lock design keeps the money exact under 20-way contention on one wallet.
  Contention roughly doubles the hot wallet's latency (p50 83.8 ms against 43.5 ms).
- The database here holds only the suite's small dataset, not 5 M rows.
- Client and server share a laptop that was already busy.
- It is a closed-loop test at about 340 requests a second. It is not the proposed open-loop target
  (§8) of 50 requests a second against staging.

## 8. NOT EXECUTED

- **k6 scripts** (`tools/load/`): `seed.js`, `online-fueling.js`, `offline-sync.js`,
  `portal-reads.js`, `vehicle-create.js`, `lib/zawed.js`, and `README.md` with seeding for 10, 30
  and 100 companies, the scenarios and stages, and the **PROPOSED** acceptance targets:
  - online fueling p95 < 300 ms at 50 req/s;
  - error rate < 0.1 %;
  - zero deadlocks.

  k6 is not installed and nothing was installed, so **none of them has been run** against any
  environment, and none of the PROPOSED targets has been tested.
- No measurement on production-like hardware, network or a managed PostgreSQL.
- No ledger rows in the 5 M dataset, so the statement, reconciliation and ledger reads at scale were
  not measured (§7.2 of the audit lists them).
- The offline sync path and the portal reads were not load-tested over HTTP. Only online fuelings
  and the vehicle and tag setup calls were.
- The generic-plan risk was measured only as a plan:
  - With `force_generic_plan`, the Ops "newest" list becomes a parallel seq scan of all 5 M rows:
    2,458 ms instead of 4 ms (run 1).
  - In the default `auto` mode, PostgreSQL keeps custom plans when the generic plan is estimated to
    cost more.
  - Whether pgjdbc's server-prepared statements ever switch these queries to their generic plan in
    production was not observed.

## 9. Verification

- `QueryPlanReport` ran at full scale: run 1 completed. Run 2 was stopped by the hang described in
  §6. Run 3 completed with `baseline`, `v16` and `v16_concurrent`.
- `OnlineFuelingLoadMeasurement` passed: all assertions on money, outcomes and deadlocks held.
- `mvnw -B clean verify`, the full normal suite, ran with V16 present:
  - 283 tests, 0 failures, 0 errors, BUILD SUCCESS, in 4 min 51 s;
  - Flyway applied V1–V11 and V16 in one go (V12–V15 are not on this branch);
  - V16 ran in a transaction, without the `[non-transactional]` marker.
- The two measurement classes did not run in that suite: their names match no Surefire pattern.

## 10. Files

| File | What |
|---|---|
| `apps/api/src/test/java/com/zawed/api/perf/QueryPlanReport.java` | Dataset, query plans, candidate indexes, write cost, V16 application (disabled by default) |
| `apps/api/src/test/java/com/zawed/api/fueling/OnlineFuelingLoadMeasurement.java` | Concurrent online fuelings over HTTP with money checks (disabled by default) |
| `apps/api/src/main/resources/db/migration/V16__fuelings_time_indexes.sql` | The three indexes |
| `tools/load/**` | k6 scripts and README (NOT EXECUTED) |
| `docs/artifacts/production-hardening/perf-evidence/` | Raw summaries of runs 1 and 3, environment, plan files, load measurement output |
