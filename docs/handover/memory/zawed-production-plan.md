---
name: zawed-production-plan
description: "ZAWED production build (Spring Boot + React), phases B0–B8; B0–B6 + production hardening on develop and zawed-dev since 2026-11-19 (schema 16); review findings #1 import poison row and #2 optional idempotency key on adjustments block production; local machine has no Docker"
metadata:
  node_type: memory
  type: project
  originSessionId: 6173e5d3-0c08-4b32-9c64-0c49d2aa806d
  modified: 2026-11-19T12:03:30.493Z
---

On 2026-11-12 the user asked to move from the investor demo to the real build with Spring Boot + React. The plan is in `docs/architecture/implementation-plan.md` (untracked on `develop`, status Proposed). It defines build phases B0–B8 (B0 foundations → B1 identity → B2 master data → B3 ledger → B4 online engine → B5 device/offline sync → B6 station finance → B7 hardening; B8 Kotlin POS in parallel after the hardware spike) and 15 proposed ADRs (Java 25, Maven, Spring Boot 4.x + Modulith, Flyway, JPA + plain SQL for money, embedded PostgreSQL for tests, Railway env `zawed-dev`).

2026-11-12: the user said "يلا ابدا شغل", taken as approval of §12 as recommended. B0 was built the same day (uncommitted on `develop`): `apps/api` Spring Boot 4.1.2 / Java 25 / Modulith 2.1.1 / springdoc 3.2.0, tests on embedded PostgreSQL 18 (Zonky 2.2.2), 52 API tests and 154 web tests green, E2E green, ADRs in `docs/adr/`, summary in `docs/summaries/B0.md`, CI in `.github/workflows/ci.yml`. B0 was committed, pushed and deployed to Railway project `zawed-dev` the same night. On 2026-11-13 the user said to drop the demo entirely and focus on a senior-grade production backend. B1 (identity) was built in the working tree, uncommitted: contract in `docs/architecture/b1-identity-contract.md`, summary in `docs/summaries/B1.md`, 103 API tests and 171 web tests green. The `zawed-deep` agent hit the Fable 5-hour limit after writing V2/V3 migrations and config; the main session (Opus) wrote the rest. B1 was then committed (5573f16, d58005e) and deployed to `zawed-dev`. Later on 2026-11-13 the user, away from home, said "do everything yourself and continue into the next phase" (durable authorization to commit, push to develop and deploy to `zawed-dev` for this run of work). B2 (master data: tenancy, fleet, network, pricing modules; V4, V5) was built, Fable-reviewed twice (B1 fixes and B2 fixes applied), committed as 854bb91 and pushed. Key decision: V5 drops `FORCE ROW LEVEL SECURITY` (a non-superuser owner broke sign-in; `ALTER FUNCTION ... SET app.*` is refused to non-superusers) and `ScopedTransactionManager` refuses any transaction not running as `zawed_app`. `npm run seed:api` (apps/web) builds the demo master data through the API. B2 web followed the same day (8c8b712: live screens in `apps/web/src/live`, used only when VITE_API_MODE=http; mock-mode demo screens untouched; routes without an API yet show `NotYetLive`), then user management and the audit log (f4c4dd5), and the Railway IaC migration (6992be0). B3 (ledger, wallet, top-ups, reconciliation) was started by `zawed-deep` on 2026-11-13; Fable then failed with a Cloudflare 403 twice, so Opus finished, fixed (balance trigger CASE, ON CONFLICT for station payables) and shipped it with its web screens as 56631e5 (schema 6 on zawed-dev). The Fable security review of B3 was pending at that point. Next: B4 fueling engine (money path). Open owner questions in docs/summaries/B3.md (who may request top-ups; offline negative balances; platform fee legs).

B4 (device channel + online fueling engine, V7/V7_1) shipped 2026-11-13: API 3b95e3a, web 25886ce (live POS with WebCrypto device key, fuelings/alerts screens by Sonnet), Fable review fixes e8c41cc, summary 2394233; 235 API tests, 187 web tests, browser walk-through 19/19 (scratchpad b4-browser.mjs against `mvnw spring-boot:test-run` + `VITE_API_MODE=http npm run dev`). VIP ships switched OFF (`zawed.fueling.vip-enabled=false`) until the owner decides who pays for VIP fuel. Next: B5 offline sync (needs the owner's offline negative-balance decision).

Windows quirk: when the session's working directory starts with a lowercase `c:`, vitest fails with "failed to find the runner" (the module loads twice under two drive-letter cases). Run `npm run check`/vitest from PowerShell after `Set-Location 'C:\Users\...'` (upper-case C).

Reference repos the user wants patterns taken from: `D:\my startups\darsly` (Nagahny, NestJS + Prisma: auth/session rotation, device enrolment, double-entry ledger, error contract; see its `ARCHITECTURE_REVIEW.md` and `docs/SYSTEM.md`) and `D:\projects\soker` (Next.js + Prisma ERP: authz helper, audit-in-transaction, backups/DR; see `docs/AUDIT.md`). The PRD PDF is at `C:\Users\ae11w\Downloads\Fleet_Fuel_Management_PRD_Final__v6.pdf`.

Same day the user asked for automatic model routing and for Darsly's skills in this repo. Set up (all untracked, nothing committed): `CLAUDE.md` with the routing table, `.claude/settings.json` (`model: opus`, frontend-design plugin), agents `zawed-deep` and `zawed-reviewer` (Fable) and `zawed-bulk` (Sonnet), skills `impeccable` and `webapp-testing` copied as-is, and `zawed-design-system`, `zawed-ui-audit`, `zawed-release-gate` written for this codebase. Three Darsly skills were deliberately not ported because ZAWED has no counterpart yet: `nagahny-ci` (write `zawed-ci` when B0 creates the CI workflow), `nagahny-code-map` (ported on 2026-11-19 as `zawed-code-map`, see [[zawed-skill-pipeline]]) and `nagahny-feature-sync` (no Help Center or assistant here).

Local toolchain checked that day: JDK 25 (Temurin) and JDK 21, Maven 3.9.11 at `D:\spring`, Node 22, Railway CLI. No Docker, no PostgreSQL, no Gradle, no `gh`.

**Why:** the user wants controlled phases with approval before each one; the plan is the baseline to approve.
**How to apply:** before backend work, check whether §12 was approved and start at B0 step 1; do not assume Docker exists; keep [[zawed-git-policy]] (develop only, `zood-web` frozen). See [[zood-project-context]].

B5 (offline capture and sync, V8) shipped 2026-11-15 as 8f17afc: snapshot with tag digests (HMAC per-device key, no UIDs), capped wallet allowance (no balances), no PIN hashes; POST /pos/sync always records offline captures (conflicts + SYNC_CONFLICT alert); the wallet shortfall goes to a COMPANY_RECEIVABLE account (policy-neutral until the owner decides who bears offline overspend; the user said on 2026-11-15 to leave policies for later and keep building). Fable wrote the ingest core then hit its 5-hour limit; Opus finished it. 267 API tests, 197 web tests, walk-through 11/11 (scratchpad b5-browser.mjs). Swagger UI at /api/v1/docs (081eebe), on unless ZAWED_API_DOCS=false; requested by the user so others can read and use the API.

2026-11-15 later: Fable reviewed B5 (3 High, 5 Medium, 5 Low). Fable then hit its limit (resets Nov 18, 3am Cairo); Opus fixed most and pushed 0249d99 (V8_1: dispensed fuel for a known tag always recorded with conflicts AGENT_UNVERIFIED/DEVICE_CLOCK_SKEW/DECLINED_ONLINE/STATION_NOT_ACTIVE/PRICE_MISMATCH/OVER_OFFLINE_ALLOWANCE, payable_held, offline exposure cap; snapshot digests cut to 32 bits; terminal keeps synced captures until a newer snapshot, dead-letters rejected ones, saves attempts). NOT DEPLOYED to zawed-dev yet (zawed-dev runs 081eebe = B5 + Swagger). Still to do for B5: POST /pos/fuelings/{clientTxnId}/resolve endpoint + OUTCOME_MISMATCH alert (the web already calls it), per-item RETRY in OfflineSync, update b5-sync-contract.md and ADR 0019 for these changes, run the full API suite once more, B5 walk-through again, docs/summaries/B5.md, deploy. Then B6.

Update: the remaining B5 items were finished in 0e3dbc8 (resolve endpoint, per-item RETRY, contract/ADR updated) and B5 is DEPLOYED to zawed-dev (schema 8.1). Summary in docs/summaries/B5.md. Next: B6 station finance (exclude payable_held from payouts; report receivables). Fable is out of quota until 2026-11-18 03:00 Cairo: re-review B5 fixes then.

B6 (station finance, tickets, exports, activity calendar, shifts; V9–V11) shipped 2026-11-19: API d2de536 (deployed, schema 11), web ba056b3 (Sonnet built the screens). Fable wrote the V9/ledger/stationfinance Ops core, then ran out of usage credits; Opus finished it (portal endpoints, tickets, tests, matrix, contract, ADR 0020, docs/summaries/B6.md). 283 API tests green. PENDING: Fable review of B5 fixes and of B6 money code. Docs for a second developer: docs/architecture/database-erd.md (incl. multi-tenancy section) and pos-and-driver-app.md; shareable pages: English https://claude.ai/artifact/4H7ypdsG1wAEVT15c6k9gu and Arabic RTL (updated to V16, current account) https://claude.ai/artifact/8oYuQKkMw1ApvK6pcxFz5V; the older links belong to a previous account and cannot be updated (builders in the session scratchpad; the Arabic page reuses the English docs' Mermaid blocks by index). Deploy gotcha hit again: `railway up` must run from deploy-wt/apps/api or deploy-wt/apps/web, not the worktree root (root build fails in Railpack with "No start command").

2026-11-19 production hardening (owner's autonomous mission): branch feature/zawed-production-hardening (pushed, NOT merged, NOT deployed), head 1a790f6. V12 maker-checker top-ups (verified amount; OPS_MANAGER role with TOPUP_APPROVE; wallet_adjustments via platform:adjustments), V13 idempotency_keys, V14 fleet identity/plate canonical/fleet_code/nonce purge/company offline alert, V15 bulk vehicle import (CSV/XLSX, fastexcel, PostgreSQL worker with lease), V16 fueling time indexes (measured on 5M rows). API 311 tests green; web check/build/e2e green. Handover: docs/artifacts/production-hardening/final-handover.md. Pending: Fable review, browser walkthrough, k6 run, owner decisions (offline receivable ceiling, CR uniqueness, plate category). Perf data left at D:\zawed-perf\pgdata (7.4 GB). Bash heredocs with apostrophes or Java text blocks fail in this harness: write scripts with the Write tool.

Update 2026-11-19 (afternoon): on the owner's request, feature/zawed-production-hardening was fast-forwarded into develop (5daac5d) and pushed. It was deployed to zawed-dev from a clean worktree, the API first and then the web. Schema 16 is live: V12–V16 applied at 12:18:52 UTC.
- Pre-checks: the database was nearly empty (one SUPER_ADMIN, no companies, vehicles or fuelings). No plate collisions, no tank over 1,500 L.
- Backup: C:\Users\ae11w\zawed-backups\zawed-dev-before-V12-20261119-1415.sql.
- Gate: API 311/311 green (the first run failed only because embedded Postgres hit "Address already in use" while the web suite ran in parallel; run the API suite alone). Web 204/204 unit tests, build green, E2E 50 passed plus 1 flaky (the presenter panel in shell.spec).
- Review: done on Opus (Fable is gone). The High and Medium findings, which must be fixed before production, are listed in docs/artifacts/production-hardening/final-handover.md, limitation 1.
- Owner to do: create an OPS_MANAGER user on zawed-dev.
- Database access: zawed-dev Postgres has no public URL. Use `railway ssh --service Postgres -- psql -U postgres -d railway -f - < file.sql` (from apps/api, which is linked), and wrap checks in BEGIN READ ONLY.
- 2026-11-19 12:32 UTC: at the owner's explicit choice, the zawed-dev Super Admin (the only user) got a new password set directly in the database. It is below the 12-character policy; the owner was told and accepted. The Argon2id hash used PasswordHasher's parameters, an audit_log row PASSWORD_RESET_OUT_OF_BAND was written, and sign-in was verified (it answers MFA_ENROLLMENT_REQUIRED; MFA is not enrolled yet). Never store the password in memory or docs. Before any production use, set a policy-compliant password through the UI.

2026-11-19 (evening): B7 started on branch feature/b7-hardening, from develop f3b5827. Nothing is committed yet: the owner has not asked. The plan, with the owner's decisions and progress, is docs/architecture/b7-plan.md.
- Owner's decisions: backups and PITR are deferred to the pilot host (runbook only); errors go to Sentry; staging is a test tenant inside zawed-dev; notifications are in-app only.
- Done in the main session:
  - import worker fixes;
  - Sentry wiring (API starter 8.63.0 plus SentryScrubber; web @sentry/browser 11.11.0 loaded on demand, because @sentry/react bloated the entry chunk);
  - docs/runbooks/ (a restore rehearsal took 9 s);
  - docs/security/review-pack.md.
- Delegated to zawed-deep (Opus), each in its own scratchpad worktree:
  - the money fixes (feature/b7-w1-money, V17);
  - the IDOR sweep (feature/b7-w2-idor).
- Subagents dropped twice with "SSL certificate verification failed". Resume them with SendMessage, which keeps their context.
- New gap, W9: ZAWED_DATA_KEY cannot be rotated, because DataEncryptor has a single key.
- Pending from the owner: the Sentry DSNs, and approval to commit.

2026-11-19 (night), owner decisions:
- MFA is switched off on zawed-dev for testing ("شيلها دلوقتي ونرجعها بعدين"). This is a flag, not deleted code: zawed.security.mfa-required / ZAWED_MFA_REQUIRED=false. DeploymentGuard refuses it in production, and the default is true. Re-enable it before any real use.
- Sentry is postponed: the code stays in, switched off until a DSN is set.
- The owner wants test accounts for every role on zawed-dev.
- A UI revamp is added as phase U1, after B7 and before the pilot, in implementation-plan.md §7. The owner found many UI bugs and the screens are not comfortable yet.

2026-11-20: B7 main work committed and pushed on feature/b7-hardening (c6198e7, plus 9b14811 declaring ZAWED_MFA_REQUIRED in .railway/railway.ts). The owner approved the commit and deploy explicitly; the auto-mode classifier refused an inferred commit before that. Deployed the API and the web to zawed-dev from a clean worktree; schema 16, unchanged.
- ZAWED_MFA_REQUIRED=false is set on zawed-api. The Super Admin signs in with the password alone (verified).
- Demo data seeded (npm run seed:api): 3 companies, 50 vehicles, 1 operator (Elite Station Management), 2 stations, 6 devices pending activation, 3 agents, prices.
- Test accounts, all with the shared password written in C:\Users\ae11w\zawed-test-accounts\accounts.md:
  - test.ops@zawed.test
  - test.ops.manager@zawed.test
  - test.fleet.manager@zawed.test and test.accountant@zawed.test (Nile Transport & Services)
  - test.station.manager@zawed.test
- Nile's wallet holds 500,000 EGP from one confirmed top-up.
- The device codes and agent PINs are in zawed-dev-device-codes-and-agent-pins.json in that same folder. The local seed-secrets file was restored.
- Full API suite: 322/322 on a quiet machine. Parallel agent Maven runs cause false failures (BindException, "Permission denied: connect", context failure threshold), so run the gate suite alone.
- Still unmerged: the agent branches feature/b7-w1-money (review fixes in progress), feature/b7-w2-idor, feature/b7-w5-notifications and feature/b7-w9-datakey.

2026-11-20, owner decisions:
- One sign-in page for every role, the Super Admin included. This removes the separate admin door: B7 W10, auth work, so it goes to zawed-deep.
- A modern animated public landing page at "/", with a "request a demo" form that sends a lead to Ops. It is part of U1 (implementation-plan.md §7, U1 step 5).
- When the owner tried test.ops with the Super Admin's password he got "invalid credentials". The test accounts share their own password (in accounts.md); only the Super Admin uses his own password.
