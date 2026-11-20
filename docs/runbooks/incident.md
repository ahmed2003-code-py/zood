# Incident response

## 1. Decide how bad it is (first 5 minutes)

| Severity | Examples | Response |
|---|---|---|
| **SEV1** | Fuelings fail at stations; money is wrong (a wallet or a payable disagrees with the ledger); data of one company is visible to another; a secret leaked | Act now, tell the owner now, and keep a timeline |
| **SEV2** | A portal is down but fuelings work (online or offline); imports or exports stuck; sync delayed | Fix within the day |
| **SEV3** | A screen error or a wrong label, with a workaround | Normal work queue |

**Fuelings keep working offline.** A POS that loses the API records captures and syncs them later. An API outage is SEV1 only if devices cannot fuel at all, for example when they were not yet activated or their snapshot expired.

## 2. Look

- **Is the API up?** Check `/api/v1/health` and `/api/v1/meta`, then the Railway deploy logs and the service metrics (CPU, memory, restarts). `/api/v1/health` also answers `DOWN` when a scheduled job (the nightly reconciliation, a retention purge) has had no success within its window, and `WARN` when its latest run failed. `/api/v1/health/readiness` stays `UP` in both cases. See [job-health.md](job-health.md).
- **What failed?** Look at Sentry once a DSN is set. Events carry the exception type and stack, with no request data. Also read the API logs: JSON lines, each with a `requestId` you can follow through one request.
- **Is money consistent?**
  - The nightly reconciliation logs `Reconciliation … found N difference(s)` when it finds any.
  - Did it run at all? `GET /api/v1/ops/jobs`: `ledger-reconciliation` must be `OK`, with `lastSuccessCounts.findings` at 0 ([job-health.md](job-health.md)).
  - Run it now through the API, or check one wallet: its cached balance must equal the sum of its ledger entries.
- **Is data isolated?** RLS on every tenant table is the second wall. A suspected leak is SEV1 even when unconfirmed.

## 3. Contain

| Situation | Contain by |
|---|---|
| A bad deploy | Roll forward with a fix ([deploy.md](deploy.md)). Code cannot simply be rolled back past a migration. |
| A stuck import | A job `RUNNING` with `lease_until` long past means no worker is running. Check that `zawed.fleet-import.worker-enabled` is true. A row that fails is now recorded as `FAILED` and does not block the job (ADR 0026). |
| A wrong wallet or payable | Freeze the decision, not the platform: no adjustment, payout or reversal on it until it is explained. Corrections go through the two-person adjustment or reversal flows, never through SQL. |
| A lost device | Follow [secrets-and-devices.md](secrets-and-devices.md). |
| A leaked secret | Follow [secrets-and-devices.md](secrets-and-devices.md). For `ZAWED_DATA_KEY`, read the warning there first. |
| A suspected cross-tenant leak | Take the affected endpoint or screen down (feature flag, or a hotfix that refuses the request), keep the logs, and tell the owner. Fix it with a regression test in the authorisation matrix and the IDOR sweep. |

## 4. Recover and record

- Verify with the checks in [deploy.md](deploy.md) → Verify.
- Write an incident note: the timeline, the impact (which companies and stations, and amounts if any), the cause, the fix, and the follow-up. Put it under `docs/incidents/<date>-<slug>.md`, in English, without personal data.
- Add the follow-up to the plan. Update `docs/architecture/known-issues.md` if it can happen again.
