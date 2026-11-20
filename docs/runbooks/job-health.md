# Reading job health

Use this when `/api/v1/health` answers `DOWN` or `WARN`, after every deploy, or when you want to know whether the nightly reconciliation and the retention jobs are running. Background: ADR 0028.

## 1. The quick look

`GET /api/v1/health` (public, no details):

| Answer | HTTP | Means |
|---|---|---|
| `UP` | 200 | The database answers and every scheduled job succeeded within its window |
| `WARN` | 200 | A job's latest attempt failed, but its last success is still within its window. Look today |
| `DOWN` | 503 | A job has had **no success within its window**, or the database is down. Look now |

- **Probes.** `/api/v1/health/readiness` (what Railway checks on deploy) and `/api/v1/health/liveness` do not include the jobs. So a late job never blocks a deploy or restarts the service. Only the overall `/api/v1/health` shows it.
- **Caching.** The answer is cached for 30 seconds.

## 2. Which job, and why

Sign in as Super Admin, Ops Manager or Ops and call `GET /api/v1/ops/jobs`. There is no screen yet. Use the API reference at `/api/v1/docs`, or `curl` with the access token.

Each job comes back with:
- **`state`:**
  - `OK`;
  - `PENDING`: not run yet since this instance started;
  - `FAILING`: the latest attempt failed, but the last success is still within the window;
  - `OVERDUE`: no success within the window;
- **`maxAge`** (the window) and **`dueBy`** (when it turns `OVERDUE` without another success);
- **`lastSuccessAt`** and **`lastSuccessCounts`** (numbers only, for example `{"sessions": 12, "mfaChallenges": 3}`);
- **`lastFailureAt`** and **`lastFailureErrorClass`** (the exception class, never its message).

| Job | Runs | Window (`maxAge`) | What a late run means |
|---|---|---|---|
| `ledger-reconciliation` | 02:30 Cairo, nightly | 27 h | **Ledger drift would go unnoticed.** Treat as SEV2. It becomes SEV1 if the last run's findings are not zero ([incident.md](incident.md)) |
| `nonce-retention` | every 15 min | 1 h | `device_nonces` grows; replay protection is unaffected |
| `idempotency-key-retention` | every 6 h | 14 h | `idempotency_keys` grows |
| `session-retention` | hourly | 4 h | Ended sessions and expired MFA challenges are kept longer than ADR 0028 says |
| `job-run-retention` | every 6 h | 26 h | `job_runs` grows |
| `notification-retention` | every 6 h | 14 h | `notifications` keep more than 90 days |
| `data-key-rotation` | every 10 min (when enabled) | 1 h | Values stay under an old data key, so it cannot be retired ([secrets-and-devices.md](secrets-and-devices.md)). `SKIPPED_LOCKED` here means this instance's current key is not the deployment's |

**History:** `GET /api/v1/ops/jobs/runs?job=<name>` lists the runs, newest first (25 per page, `?cursor=` for more). Each run shows its `outcome`, its `instance` (`pid@host`, to tell replicas apart), its times, its counts and, for a failure, its `errorClass`.
- **`SKIPPED_LOCKED`** is normal when two instances run: the reconciliation runs on one instance a night, and the other records a skip.
- **A long run of `SKIPPED_LOCKED` with no `SUCCESS` anywhere** means the lock holder never finishes. Look for a stuck session holding the advisory lock (§3).

## 3. What to do

1. **Find the error.**
   - Search the API logs for `Job <name> failed`. The line carries the stack trace; `requestId` is empty for jobs.
   - With Sentry on, the same error is an event.
   - `errorClass` tells you the kind of failure: for example `org.springframework.dao.DataAccessResourceFailureException` is the database, and `org.springframework.dao.DataIntegrityViolationException` is a refused row.
2. **The database is down?** Then `/api/v1/health/readiness` is down too. Follow [incident.md](incident.md) first. The jobs catch up on their next run once it is back.
3. **The reconciliation did not run?**
   - Check for an instance: Railway may show none running at 02:30, after a crash or a long deploy.
   - Then run it by hand as Super Admin: `POST /api/v1/ops/reconciliation/runs` (it answers `409 RECONCILIATION_RUNNING` while another run holds the lock). Read its findings.
   - A manual run is not a scheduled run, so the job stays `OVERDUE` until the next 02:30 succeeds. Note that in the incident timeline.
4. **A stuck lock?** Read-only check:

   ```sql
   BEGIN READ ONLY;
   SELECT pid, state, now() - xact_start AS running_for, left(query, 80)
     FROM pg_stat_activity
    WHERE query ILIKE '%ledger_reconcile%' AND pid <> pg_backend_pid();
   ROLLBACK;
   ```

   A reconciliation runs for seconds. One running for hours is stuck. Ending it (`pg_cancel_backend(pid)`) is a write on production: do it only with the owner's agreement, and record it.
5. **A purge keeps failing?** Its function refuses a cutoff inside its floor. A setting such as `ZAWED_SESSIONS_RETENTION` below the floor is raised to the minimum by the job, so a refusal there is a clock problem: the server clock is ahead of the database's by more than a day. Compare `date -u` on the service with `SELECT now()`.

## 4. Settings

The defaults suit the pilot. Change them only with a reason recorded in the plan.

| Setting (environment variable) | Default | Floor |
|---|---|---|
| `zawed.jobs.max-age.<job>` (`ZAWED_JOBS_MAXAGE_<JOB>`, without the dashes, e.g. `ZAWED_JOBS_MAXAGE_LEDGERRECONCILIATION=PT30H`) | per job, above | — |
| `zawed.sessions.retention` (`ZAWED_SESSIONS_RETENTION`) | `P90D` | 31 days (database: 30) |
| `zawed.sessions.mfa-challenge-retention` (`ZAWED_SESSIONS_MFACHALLENGERETENTION`) | `P1D` | 2 hours (database: 1) |
| `zawed.sessions.purge-interval` | `PT1H` | — |
| `zawed.jobs.run-retention` (`ZAWED_JOBS_RUNRETENTION`) | `P90D` | 31 days (database: 30) |
| `zawed.jobs.run-purge-interval` | `PT6H` | — |

Add any new variable to `.railway/railway.ts` with `preserve()`, as for every other variable.

## 5. It worked when

- `GET /api/v1/ops/jobs` shows every job `OK`, or `PENDING` within a few minutes of a deploy: the first purges run 2 to 10 minutes after start, and the reconciliation at the next 02:30.
- `/api/v1/health` answers `UP`.
