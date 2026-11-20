# ADR 0028: Retention, and a record of every scheduled job run

**Status:** Accepted, 2026-11-20 · **Source:** B7 W4 (`docs/architecture/b7-plan.md`), V21

## Context
- **Sign-in tables grew for ever.** Nothing removed a web session or an MFA challenge. V2 kept every revoked session "for the record", with no end. `sessions` gets one row per sign-in. `mfa_challenges` gets one per password step of a role with a second factor. An unfinished enrolment's challenge still holds the pending TOTP secret (encrypted).
- **Jobs failed silently.** Scheduled jobs ran under `@Scheduled` and left nothing behind but log lines. A job that stopped, such as the nightly reconciliation, could not be seen. Ledger drift would then go unnoticed.
- **A table that must not shrink.** `agent_sessions` (V7) is a different table: a station agent's shift on a device. Shifts and the "last shift" period are built from it, so it is kept.

## Decision

### Every retention rule in the system

| Data | Rule | Floor in the database | Job, interval | Where |
|---|---|---|---|---|
| `sessions` (web sign-ins) | Deleted **90 days after it ended**, where "ended" is the earliest of `revoked_at`, `expires_at` (idle) and `absolute_expires_at`. Setting: `zawed.sessions.retention`. | 30 days (`purge_sessions`). The job never asks for less than 31. | `identity.SessionRetention`, hourly | V21, this ADR |
| `mfa_challenges` | Deleted **1 day after it expired**. Setting: `zawed.sessions.mfa-challenge-retention`. | 1 hour (`purge_mfa_challenges`). The job never asks for less than 2. | same job | V21, this ADR |
| `agent_sessions` (shifts) | **Kept for ever.** No function touches the table, and the runtime role has no DELETE on it. | — | — | V7, this ADR |
| `idempotency_keys` | Deleted **30 days** after creation. Setting: `zawed.idempotency.key-retention`. | 7 days. The job never asks for less than 8. | `platform.persistence.IdempotencyKeyRetention`, every 6 h | V17, ADR 0022 |
| `device_nonces` | Deleted **1 hour** after creation. Setting: `zawed.pos.nonce-retention`. | 15 minutes. | `network.NonceRetention`, every 15 min | V14, ADR 0024 |
| `notifications` (and their read rows) | Deleted **90 days** after creation. Setting: `zawed.notifications.retention`. | 30 days (`purge_notifications`). | `notifications.NotificationRetention`, every 6 h | V19, W5 |
| `job_runs` | Deleted **90 days** after the run started. Setting: `zawed.jobs.run-retention`. | 30 days (`purge_job_runs`). The job never asks for less than 31. | `platform.jobs.JobRunRetention`, every 6 h | V21, this ADR |
| `leads` (demo requests, U1) | A **`CLOSED`** lead is deleted **24 months after it was closed** (`closed_at`; reopening clears it and stops the clock). Open leads are kept: they are Operations' work in progress (owner question in `u1-leads-contract.md` §9). The `LEAD_UPDATED` audit rows stay; they carry the lead's id and reference, no personal data. Setting: `zawed.leads.closed-retention`. | 90 days after closing (`purge_closed_leads`). The job never asks for less than 91. | `leads.LeadRetention`, daily | V22, `u1-leads-contract.md` §6 |
| `rate_limit_counters` (U1) | Deleted once their window has ended: at most a day for the demo form's buckets. Only keyed hashes of an address or a contact are stored. | Only ended windows (`purge_rate_limit_counters`): a running window is never removed, so a purge cannot reset a limit. | `platform.ratelimit.RateLimitCounterRetention`, hourly | V22, ADR 0012 |
| Import files | **Never stored.** Only each row's normalised values and errors are kept (`fleet_import_jobs`, `fleet_import_rows`). | — | — | V15, ADR 0026 |
| `fleet_import_jobs`, `fleet_import_rows` | Kept for now: no rule yet (owner question below). | — | — | V15 |
| `audit_log`, ledger transactions and entries, `fuelings`, reversals, payouts, `reconciliation_runs` and findings, alerts, tickets, `users` (deactivated, never deleted) | **Kept for ever.** They are append-only records, or never removed by design. | — | — | V2–V12 |

### Why these windows
- **Sessions: 90 days after the end.** A session row holds what an investigation needs: when the user signed in and was last seen, the device (user agent), the address prefix (/24 or /48 only) and why the session ended (logout, reuse detected, signed out elsewhere, password change). Suspicious sign-ins tend to come up weeks after the fact: a disputed fueling, a reviewer's question, a month-end statement. 90 days covers a full quarter of that.
  - **The audit log keeps the essentials for ever:** `LOGIN_SUCCEEDED` carries the session id, plus `LOGOUT`, `SESSION_REVOKED` and `MFA_FAILED`. What the purge drops is the device and network detail, which is personal data and should not be kept longer than it is useful.
  - **The row lives at most 120 days:** 30 days of use (the absolute lifetime), then 90.
  - **The 30-day floor** stops a misconfigured call from erasing last month's evidence.
- **MFA challenges: 1 day after expiry.** A challenge lives 5 or 10 minutes and is single use. Wrong codes are already in the audit log (`MFA_FAILED`), so the row adds nothing once it has expired. One day leaves time to look at a burst of sign-in attempts the same day. Purging promptly removes pending TOTP secrets of enrolments nobody finished. The 1-hour floor sits well past the 10-minute maximum lifetime.
- **Job runs: 90 days.** About 280 runs a day per instance (data-key rotation every 10 minutes, nonces every 15, the rest hourly to daily) is about 25,000 rows: a quarter of history, enough to see a pattern of failures. Reconciliation keeps its own runs and findings for ever in `reconciliation_runs`.

### How a purge is safe
- **The pattern:** V14's `purge_device_nonces` and V17's `purge_idempotency_keys`.
  - Each purge is a bounded `SECURITY DEFINER` function, owned by the migration user, with `search_path` pinned. It is revoked from `PUBLIC` and executable by `zawed_app`.
  - It refuses a cutoff inside its floor, or a null one, whoever calls it.
  - It deletes at most `p_limit` rows (at most 50,000) per call. The jobs ask for 5,000 per batch, each batch its own short transaction, and stop after 40 batches per run.
  - `zawed_app` has **no DELETE** on `sessions`, `mfa_challenges`, `agent_sessions` or `job_runs`. The functions are the only way out.
- **No purge can delete a live session.** "Ended" is the earliest of the three end times. A live session's three are all in the future (or null for `revoked_at`), so no cutoff in the past can reach it, however old the session is. `Sessions.usableAt` applies the same rule. An expression index (`sessions_ended_at_idx`) serves the query.
- **No conflict with sign-in or refresh.** Rows are chosen with `FOR UPDATE SKIP LOCKED`, so a purge never waits behind a request and never makes one wait. It only ever chooses dead rows; refresh and logout update only live ones. The ended-before test is repeated on the row being deleted, as a second line of defence.
- **A refresh token presented after its session was purged** answers `SESSION_EXPIRED`, not `REFRESH_REUSE_DETECTED`. The session had already ended at least 90 days before, so nothing is lost but the extra log line.

### The job record (`platform.jobs`)
- **One run, one row.** `Jobs.run(job, body)` wraps the body of every scheduled job that works on the database. After the body, in a transaction of its own, it records one row in `job_runs` through `job_run_record` (`SECURITY DEFINER`):
  - the job name, the instance (`pid@host`), when it started and finished;
  - the outcome: `SUCCESS`, `FAILED` or `SKIPPED_LOCKED`;
  - counts, as a flat jsonb of non-negative integers under a **fixed list of names** (`job_run_count_keys()` in V21, `JobResult.COUNT_KEYS` in Java, kept equal by `JobsTests`). A pattern of identifier keys was not enough: it would let a value such as a phone number be written as a key;
  - for a failure, the exception's class name.
- **Nothing private can be written.** The database checks every field: no value, name, message or personal data fits. A run may not claim to finish in the future.
- **The wrapper adds no lock.** Each job keeps its own:
  - the reconciliation's advisory lock inside `ledger_reconcile`. When it is held, or tonight's run is already done, the run is `SKIPPED_LOCKED`;
  - none for the purges, where two instances only share the work.
- **Failures:** a failure is caught, logged with its stack (as before), and recorded. If the record itself cannot be written, the job is unaffected.
- **Reads:** `job_runs` has row-level security, so it is readable in the platform scope only. `zawed_app` has `SELECT` only. `job_run_latest(names)` (`SECURITY DEFINER`) answers "latest run, latest success, latest failure" per job for the health indicator, which has no principal.
- **Wired jobs** (`ArchitectureTests` keeps the list closed):

| Job | Max age before DOWN |
|---|---|
| `ledger-reconciliation` | 27 h: two runs are 25 h apart on the night Egypt's clocks go back, plus margin |
| `notification-retention` | 14 h |
| `data-key-rotation` | 1 h (only when `zawed.data-key-rotation.enabled`) |
| `nonce-retention` | 1 h |
| `idempotency-key-retention` | 14 h |
| `session-retention` | 4 h |
| `job-run-retention` | 26 h |
| `lead-retention` | 50 h (daily) |
| `rate-limit-retention` | 3 h (hourly) |

  Each default is overridden by `zawed.jobs.max-age.<name>`.
- **Deliberately not recorded:**
  - `InMemoryRateLimiter`'s sweep: in-process, with no database work;
  - `FleetImportWorker`: it polls every 2 seconds, about 43,000 ticks a day. Its progress is already recorded on each import job (status, lease, counters), where a stall shows (incident runbook).

### Liveness
- **The rule.** Each job has a window, its max age. It is **OVERDUE** when no success came within the window. The window counts from the last success, or from the instance's start for a job that has never succeeded, so a fresh deploy is not late. It is **FAILING** when its latest attempt failed but a success is still within the window, and **PENDING** before its first run.
- **The health indicator `jobs`**, part of `/api/v1/health`, answers:
  - **DOWN** (503) when any job is OVERDUE;
  - **WARN** (200, a custom status) when any is FAILING;
  - **UP** otherwise.

  It is cached for 30 seconds, because the URL is public. Details are never shown there.
- **Probes are unaffected.** The readiness probe that Railway checks on deploy, and the liveness probe, do not include it. A late job never stops a deploy or takes an instance out of service.
- **The HTTP mapping.** A custom `http-mapping` replaces Spring Boot's defaults, so `DOWN: 503` and `OUT_OF_SERVICE: 503` are restated next to `WARN: 200`. `JobsTests` caught readiness answering 200 with the database down without them.
- **Ops console:** `GET /api/v1/ops/jobs` gives each job's state, window, due time, last run, last success with its counts, and last failure with its class. `GET /api/v1/ops/jobs/runs?job=` gives the history. Both need `OPS_READ` (Super Admin, Ops Manager, Ops), are in `AuthorizationMatrixTests`, and hold no tenant data. How to read them: `docs/runbooks/job-health.md`.

## Consequences
- **Bounded tables:** `sessions` and `mfa_challenges` stay bounded, and a dead job shows on the health URL within its window: a missed reconciliation within 27 hours.
- **Not yet watched:** no external monitor watches `/api/v1/health` yet. Until one does (or Sentry is switched on and the `Job … failed` error log reaches it), someone has to look. The runbook says how.
- **A new scheduled job** must call `Jobs.run` or be added to the reviewed exceptions in `ArchitectureTests`; a new count name goes into both lists.
- **Data-key rotation** (ADR 0027) records each run with its counts (`reencrypted`, `failed`, `remaining`, `columnErrors`). An instance whose current key is not the deployment's records `SKIPPED_LOCKED`, so an overlap that lasts turns the job DOWN. A run in which every column failed is recorded as `FAILED`. What the job does is unchanged.
- **The bounded batches:** a backlog larger than 40 batches per run (200,000 rows) finishes over several runs.
- **Tests:**
  - `SessionRetentionTests`: the boundary, the floors, no DELETE grant, `agent_sessions` untouched, refresh and sign-in racing the purge;
  - `JobsTests`: the record, the database checks, RLS, the purge, liveness and the HTTP mapping, the Ops endpoints;
  - `ReconciliationTests`: the nightly run recorded as SUCCESS, then SKIPPED_LOCKED;
  - `ArchitectureTests`: every `@Scheduled` class is recorded or is a reviewed exception.

## Owner questions
1. **Sessions:** is 90 days after a session ends the right forensic window, or does a legal or contractual rule ask for longer (or shorter, under the personal data protection law)?
2. **Fleet imports:** should `fleet_import_rows` (a company's normalised vehicle data from each import) be purged after some time, for example 180 days, keeping the job row and its counters?
