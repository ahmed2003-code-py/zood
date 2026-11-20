# B7 plan: hardening and pilot readiness

**Status:** in progress since 2026-11-19, approved by the owner ("كمل b7").
**Objective:** ZAWED is safe to run with real money in a pilot (implementation plan §7, B7).
**Out of scope:** B8, the Kotlin POS, which a separate mobile developer owns. Our side of B8 is keeping the POS contract (`b4-fueling-contract.md`, `b5-sync-contract.md`, `pos-and-driver-app.md`, the OpenAPI document) accurate and stable.

## Owner decisions (2026-11-19)

| Topic | Decision |
|---|---|
| Backups and PITR | Deferred to the pilot hosting choice. B7 writes the restore runbook and rehearses a restore from a `pg_dump`. |
| Error reporting | Sentry, for both the API and the web. It stays off until `SENTRY_DSN` is set, and no personal or financial data is sent. **Postponed by the owner (2026-11-19, later the same day):** not needed now. The code stays in, switched off, until a DSN is given. |
| Second factor on dev | **Switched off on `zawed-dev` while the owner tests** (2026-11-19), with `ZAWED_MFA_REQUIRED=false`. It is restored by removing the variable. `DeploymentGuard` refuses to start production with it off. |
| Test accounts | One account per role on `zawed-dev`, so the owner can test everything. |
| UI | Many UI bugs: the revamp is a new phase, **U1**, after B7 (`implementation-plan.md` §7). |
| Staging | An isolated test tenant inside `zawed-dev`. There is no separate Railway environment. |
| Notifications | In-app only for now. E-mail comes later. |

## Work streams, in order

| # | Stream | Owner (model routing) | Main files |
|---|---|---|---|
| W1 | Fix the review findings (`production-hardening/final-handover.md`, limitation 1) | Import worker and parser: main session. Wallet adjustments, V17 top-up guards and idempotency purge: `zawed-deep` | `fleet/FleetImportWorker.java`, `FleetImports.java`, `FleetImportFile.java`, `Vehicles.java`, `ledger/WalletAdjustments.java`, `OpsWalletController.java`, `V17__*.sql` |
| W2 | The full authorisation matrix and an automated IDOR sweep: every endpoint × every role × another tenant's ids | `zawed-deep` | `identity/AuthorizationMatrixTests.java`, a new `IdorSweepTests` |
| W3 | Observability: Sentry (API and web), scrubbing, release tags, alert rules; health and metrics | main session | `pom.xml`, `platform/observability/*`, `apps/web/src/lib/observability.ts`, `application.yaml` |
| W4 | Retention jobs and a retention ADR: idempotency keys, expired sessions, uploaded import files, nonces (already done) | `zawed-deep` (sessions), main session (the rest) | `V18__retention.sql`, `platform/retention/*`, `docs/adr/0027-retention.md` |
| W5 | In-app notifications (B6 leftover): low balance, a top-up or adjustment waiting for approval, a new hard alert, a ticket reply | main session (API), `zawed-bulk` (screens) | a new `notifications` module, `V19__notifications.sql`, the web bell and list |
| W6 | Runbooks: deploy, roll forward, restore, incident, secret rotation, device revocation, onboarding a pilot company | main session | `docs/runbooks/*.md` |
| W7 | A test tenant on `zawed-dev` (one company, one station, devices, agents, test money) and a k6 run against it | main session | `apps/web/scripts/seed-api.ts`, `tools/load/*` |
| W8 | A security review pack for the external reviewer: scope, threat model, test accounts, known limitations | main session | `docs/security/review-pack.md` |

| W10 | **One sign-in page for everyone** (owner, 2026-11-20). The Super Admin signs in at `/login` like every other role, and the role decides which portal opens. To do: remove the separate admin door (`/admin/login`, `Door.ADMIN`, the wrong-door refusal) without weakening the rest: the same "invalid credentials" answer and timing, lockout, MFA when it is on, and the audit. Amend ADR 0008. | `zawed-deep` (authentication) | `identity/SignIn.java`, `AdminAuthController.java`, `AuthController.java`, `apps/web/src/app/router.tsx`, `auth/LoginPage.tsx`, `SignInTests` |
| W9 | **Data-key rotation** (found in W6): `DataEncryptor` knows only key 1, so `ZAWED_DATA_KEY` cannot be rotated without losing MFA secrets, bank accounts and tag UIDs. Add a second key, re-encrypt in the background, then retire the old key. | `zawed-deep` | `platform/crypto/DataEncryptor.java`, a re-encryption job |

## Progress

| # | State (2026-11-19) |
|---|---|
| W1 | **Import worker and parser: done**, in the main session. Every failure of a row is recorded, and no job can stall. Counters are recomputed from the rows. `INVALID_FLEET_CODE` is checked against the same set as V14. The XML scan covers the whole part and refuses `<!ENTITY` and UTF-16. Tests and ADR 0026 were updated, and both languages were added in the web. Note: the review's example (a tab) is normalised to a space by `Sql.clean`; the real gap was other control characters. **Money part: done** by `zawed-deep` on `feature/b7-w1-money` (322 tests green; the `zawed-reviewer` pass is running). It covers:
- the key is mandatory on wallet adjustments;
- V17 top-up guards: company inserts pinned, cancel only by the requester from `PENDING`;
- a 30-day purge of idempotency keys (7-day floor);
- the company offline alert raised exactly once.

The offline alert's concurrency finding was not real, because the wallet lock already serialises it; a different duplicate-alert bug was fixed. **Owner questions:**
- Should the company offline window be judged by capture time?
- An adjustment retry answers 201, while ADR 0022 says 200. |
| W3 | **Done, waiting for the DSN.** API: `sentry-spring-boot-4-starter` 8.63.0 and `SentryScrubber`, with tests. Web: `@sentry/browser` 11.11.0, loaded on demand. The entry chunk is unchanged at 325 KiB; `@sentry/react` was rejected because it pulled the 337 KiB design-system chunk into the entry chunk. Both are off until `SENTRY_DSN` / `VITE_SENTRY_DSN` are set. |
| W6 | **Done:** `docs/runbooks/`. A restore was rehearsed from a `pg_dump` into a separate database on the `zawed-dev` server, and it took 9 s. Not yet rehearsed: a restore onto a new server (roles first), which is for the pilot host. |
| W8 | **Done:** `docs/security/review-pack.md`. |
| W5 | **API done, review findings fixed** on `feature/b7-w5-notifications` (W1 merged in; 369 tests green, 2026-11-20): the `notifications` module, V19, `/api/v1/notifications`, 11 types, contract `b7-notifications-contract.md`. Fixes: **M1** notices are written in a savepoint, a refused one is counted (`zawed.notifications.dropped`) and its cause (fueling, sync item, top-up, adjustment, ticket, payout) still commits; **M2** a per-user "read up to" mark (`notification_read_marks`), so the unread count is an index range since the mark; **L1** a user-less platform session writes only `WALLET_LOW_BALANCE` and `HARD_ALERT_RAISED`; **L2** fixed params keys per type; **L4** keyset cursor on `(created_at, id)`. V19 edited in place (never deployed). Re-reviewed 2026-11-20 (one Low fixed: an out-of-range cursor answered 500) and merged. Next: the web bell and list.<br>**Owner questions:**<br>- Should the Super Admin get approval notices?<br>- Should a transfer that Ops recorded and then rejected notify the company? |
| W2 | **Done** (`3636349`, `bf64128`), merged 2026-11-20. One real hole fixed (a company could link another operator's station to a ticket). Independent review (Opus, 2026-11-20): no High or Medium; six Low follow-ups (digest misses some tenant tables and operator ledger entries; list/array UUID inputs not auto-discovered; scoped roles hard-coded; response headers not compared; more leak markers; no DB same-tenant guard on ticket links). |
| W9 | In progress with `zawed-deep`: `feature/b7-w9-datakey` (V20). Review 2026-11-20: two Medium findings being fixed. |
| W10 | In progress with `zawed-deep` on `feature/b7-w10-login`. |

**Migration numbers:** V17 (W1) → V19 (W5) → V20 (W9, if needed). V18 stays unused, because Flyway refuses a version lower than one already applied, so W4 takes **V21**. Merge order on `feature/b7-hardening`: W1, then W5, then W9.
| W4 | Idempotency keys are covered in W1 and nonces earlier; notifications get a 90-day purge in W5. **Left:** expired and revoked `sessions`, and expired `mfa_challenges`. Nothing purges them today (checked 2026-11-19). `agent_sessions` is **kept**: shifts and the "last shift" period are built from it. This goes to the W1 agent next, using the reviewed pattern: a `SECURITY DEFINER` purge function, with no DELETE grant to the runtime role. |
| W7 | Waiting for an Ops account on `zawed-dev` from the owner. |

Every stream ends with `zawed-docs-sync` and the relevant `zawed-release-gate` steps. The streams that touch money or security (W1 money part, W2, W4 sessions) also get a `zawed-reviewer` pass.

## Assumptions

- Fable is unavailable, so `zawed-deep` and `zawed-reviewer` run on Opus.
- The external security review itself is done by an outside party. B7 delivers the pack (W8), not the review.
- W7 needs a Super Admin or Ops session on `zawed-dev`, so the owner signs in and provides a short-lived token, or creates an Ops user for the seed.
- The exit criterion "restore rehearsal meets RPO 1 h / RTO 4 h" is met with a `pg_dump` rehearsal now, and again with PITR on the pilot host.

## Exit

- No open High or Medium review finding.
- The IDOR sweep is green for every endpoint.
- Sentry receives a test error from both apps once the DSN is set.
- The retention jobs run.
- The runbooks exist, and the restore was rehearsed.
- The test tenant exists, and the k6 results are recorded.
- The review pack is ready.
