# ZAWED (زوّد) — Production Implementation Plan

**Stack:** Spring Boot (backend) + React (existing web) · PostgreSQL · Kotlin POS later
**Status:** Working baseline. On 2026-11-12 the owner said to start work ("يلا ابدا شغل") without answering §12 item by item; the §12 recommendations are followed as written and recorded in `docs/adr/`. Creating Railway `zawed-dev` still needs an explicit go-ahead.
**Date:** 2026-11-12

**Inputs read for this plan**

| Source | What it gave us |
|---|---|
| `Fleet_Fuel_Management_PRD_Final__v6.pdf` | Functional scope, roles, offline model, schema concepts, NFRs, threat list |
| `docs/competitive-analysis/zawed-roadmap-delta.md` | Pilot scope changes (station account, invoicing, price schedules, bank-change control) |
| `docs/demo/*.md`, `docs/architecture/hardware-spike.md` | Approved demo decisions, open business questions, POS hardware unknowns |
| `apps/web` (this repo) | Working prototype: 4 portals + POS simulator, typed `ZoodApi` contract, engine logic in TypeScript |
| `D:\my startups\darsly` (Nagahny: NestJS + Prisma) | Auth, sessions, device enrolment, double-entry ledger, payouts, error contract, ops lessons |
| `D:\projects\soker` (Soker ERP: Next.js + Prisma) | Accountability model, single authz helper, ledger recompute, backup and DR practice, audit findings |

**Precedence:** approved decisions > roadmap/architecture docs > PRD v6 > prototype.

---

## 1. Where we are

- The investor demo is done and frozen (`master`, tag `investor-demo-v1`, Railway `zood-web`). It runs entirely on mock data in the browser.
- Nothing is production-implemented. There is no backend, no database, no real authentication.
- The prototype is still valuable as a **specification that executes**:
  - `src/domain/fueling.ts` is the transaction engine (check order, idempotency, ledger entry, cooldown, alerts).
  - `src/domain/pos.ts` is the POS decision logic (scan evaluation, offline provisional snapshot).
  - `src/domain/money.ts` fixes the arithmetic (integer piastres, integer millilitres, half-up rounding).
  - `src/api/types.ts` (`ZoodApi`) is the seam: screens call this interface, and the mock implements it.
  - 3 unit-test files (`fueling.test.ts`, `pos.test.ts`, `money.test.ts`) are ready-made test vectors for the Java port.
- The PRD names .NET and Flutter, and also says frameworks are an engineering decision. The approved direction is Spring Boot modular monolith, PostgreSQL with RLS, Redis, object storage, Flyway, and a Kotlin POS after the hardware spike.

**Local machine (checked):** JDK 25 LTS and JDK 21 installed, Maven 3.9.11, Node 22, Railway CLI. **No Docker, no PostgreSQL, no Gradle, no `gh`.** The plan below works without Docker.

---

## 2. Scope baseline

This plan targets the **Pilot MVP** as defined in the roadmap delta §5: PRD Phase 1 core, plus four pilot-blocking additions, minus VIP and plate OCR.

| In the pilot | Not in the pilot |
|---|---|
| Multi-tenant backend, RBAC, MFA for Ops and Super Admin | VIP flow (modelled, switched off until the payer is decided) |
| Transaction engine, company wallet, immutable ledger, top-up by bank transfer | Plate OCR, pump/odometer evidence |
| Per-vehicle limits, cooldown, low-balance alerts | Driver app, dynamic limits, transaction validation (push/OTP) |
| Offline capture, sync, device binding and revocation | Station self-service, dispute workflow, ERP |
| Ops onboarding (company, vehicle, tag, station, device, agent), alert queue, sync monitor, two-person reversal, audit log | E-invoice submission module |
| Company, Station, Ops portals on real data | |
| **Delta additions:** effective-dated fuel prices pushed to devices · station financial account, payouts and statement · bank-detail change control · transaction and statement exports | |

Phases B0–B5 are the same under the PRD alone. Only B6 depends on approving the delta.

---

## 3. Target architecture

### 3.1 Repository layout

```
zood/
├── apps/
│   ├── web/          React + Vite (existing): portals and POS simulator
│   ├── api/          Spring Boot modular monolith (new)
│   └── pos/          Kotlin Android POS (after the hardware spike)
├── docs/
│   ├── adr/          one short file per decision in §4
│   ├── architecture/
│   └── summaries/    one summary per finished phase
└── .github/workflows/ci.yml
```

One repository, one API service, one database. A modular monolith keeps the money path inside one database transaction, which is the property this product depends on.

### 3.2 Backend modules (Spring Modulith, one package each)

| Module | Owns | Phase |
|---|---|---|
| `platform` | Money, Volume, ids, clock, error envelope, request id, security config | B0 |
| `identity` | Users, sessions, MFA, roles and permissions | B1 |
| `audit` | Append-only audit log | B1 |
| `tenancy` | Companies, departments, feature settings | B2 |
| `fleet` | Vehicles, RFID tags and their lifecycle | B2 |
| `network` | Station operators, stations, POS devices, agents | B2 |
| `pricing` | Effective-dated fuel price schedules | B2 |
| `policy` | Limit rules, cooldown rules | B2 (data), B4 (evaluation) |
| `ledger` | Accounts, postings, wallet balances, reconciliation | B3 |
| `rules` | Pure fueling validation (no Spring, no database) | B4 |
| `fueling` | Transaction engine, receipts | B4 |
| `alerts` | Alert queue | B4 |
| `sync` | Device snapshot, offline batch ingest, sync monitor | B5 |
| `stationfinance` | Station payables, payouts, statements, bank accounts | B6 |
| `cases` | Tickets, requests, reversals | B6 |
| `reporting` | Read models, asynchronous exports | B6 |
| `notifications` | E-mail and in-app notifications | B6 |

**Boundary rules**

1. Only `ledger` writes money. Other modules ask it to post.
2. Money postings happen synchronously, inside the caller's transaction. Everything else (notifications, live feed, soft alerts) goes through the event outbox after commit.
3. `rules` has no framework dependencies. The Kotlin POS can reuse the same code for offline validation, the way the prototype shares `checkFueling` between the engine and the POS.
4. A Modulith verification test fails the build when a module reaches into another module's internals.

### 3.3 API surface

| Prefix | Caller | Scope comes from |
|---|---|---|
| `/api/v1/auth/*` | Everyone | — |
| `/api/v1/company/*` | Fleet Manager, Accountant | The session's company |
| `/api/v1/station/*` | Station Manager | The session's operator and stations |
| `/api/v1/ops/*` | Operations | Role permissions |
| `/api/v1/admin/*` | Super Admin | Separate login and cookie |
| `/api/v1/pos/*` | POS devices | The device token and agent session |
| `/api/v1/public/receipts/{token}` | Anyone holding the link | A signed, expiring token |

The scope is always read from the token. A company id, station id or agent id in a request body is never trusted.

### 3.4 The fueling transaction (the core of the product)

```
POST /api/v1/pos/fuelings   { clientTxnId, tagUid, volumeMl, fuelType?, deviceAt, receiptLocale }

1. Look up clientTxnId. Found → return the stored outcome (replayed = true).
2. Resolve tag → vehicle → company. Station, device and agent come from the token.
3. Unit price = the price schedule in effect at server time.
4. BEGIN. Lock the company wallet row, then the tag row (always in this order).
5. Check in PRD order: cooldown → tag → vehicle → company → fuel → limits → balance.
6. INSERT the fueling row (COMPLETED or DECLINED). The row is never updated afterwards.
7. If COMPLETED: post the ledger transaction, update cached balances, set the tag cooldown,
   insert hard alerts (e.g. liters above tank capacity).
8. Write outbox events (live feed, notifications, low-balance evaluation).
9. COMMIT. A unique-key clash on clientTxnId means a concurrent duplicate: re-read and return it.
```

Offline ingest (B5) runs the same engine with `origin = offline`: evaluated at the device time, committed even when a check fails, with a `syncConflict` alert instead of a decline (PRD §7.5).

### 3.5 Ledger

Double-entry, in integer piastres. Entries are immutable; corrections are new transactions.

| Event | Debit | Credit |
|---|---|---|
| Top-up confirmed by Ops | `platform:bank` | `company:{id}:wallet` |
| Billable fueling | `company:{id}:wallet` | `station:{id}:payable` |
| Station payout | `station:{id}:payable` | `platform:bank` |
| Reversal | Mirror of the original | Mirror of the original |

Platform fee legs are added once the commercial model is decided (roadmap delta §10).

Database guarantees, not conventions:

- `fueling.client_txn_id` is unique.
- `ledger_transaction (source_type, source_id)` is unique, so one fueling, top-up or payout can never post twice.
- A constraint checks that debits equal credits in every ledger transaction.
- The runtime database role has no `UPDATE` or `DELETE` on `ledger_entry`, `fueling` and `audit_log`.
- A wallet's cached balance changes only under a row lock, in the same transaction as its entry. A nightly reconciliation job compares it to the ledger (PRD §4.2).

### 3.6 Tenant isolation (two layers, PRD §9)

- **API layer:** every query is filtered by the scope from the token. A row outside the scope answers 404, never 403.
- **Database layer:** PostgreSQL row-level security on tenant tables. Each transaction sets its scope with `set_config('app.scope_type' / 'app.scope_id', …, true)`. Migrations run as the owner role; every pooled connection switches to the runtime role `zawed_app`, which the policies bind. The owner is not forced (V5): it owns the narrow `SECURITY DEFINER` functions the sign-in path needs. Every transaction checks that it runs as `zawed_app` and refuses to start otherwise, so a connection that is still the owner can never read across tenants.
- Fuelings have two audiences (the company and the station operator), so their policy accepts either scope.

### 3.7 Web changes

The design system, i18n, themes, shell and screens stay. What changes is the data source.

| Change | Detail |
|---|---|
| HTTP client | `src/api/http-api.ts` implements `ZoodApi`. `VITE_API_MODE=mock\|http` picks the implementation, so the demo keeps working from the same code. |
| Types | Generated from the backend's OpenAPI document. No hand-written DTOs. |
| Server state | TanStack Query for fetching, caching and pagination. Zustand stays for UI state and demo mode. |
| Auth | Login screens per portal, route guards, access token in memory, refresh through an httpOnly cookie. |
| Errors | One resolver that maps the API's `code` to the existing `errors.json` copy in both languages. |
| Live updates | Server-sent events replace the cross-window `storage` events. |
| POS simulator | Becomes the protocol test client: online against the real engine in B4, real snapshot and queue in B5. The Kotlin POS then implements a protocol that is already proven. |

---

## 4. Technical decisions (proposed ADRs)

| # | Decision | Recommendation | Why |
|---|---|---|---|
| 1 | Backend language | Java 25 (LTS) | Already installed; largest Spring ecosystem; records and sealed types fit the domain |
| 2 | Build tool | Maven, with the wrapper committed | Already installed; simplest for a single service |
| 3 | Framework | Spring Boot 4.x, pinned to the latest stable at scaffold time; Spring Modulith | Approved direction; Modulith enforces module boundaries in tests |
| 4 | Persistence | Flyway SQL migrations. Spring Data JPA for master data, plain SQL (`JdbcClient`) for the ledger, engine and reports. `open-in-view` off. | The money path needs explicit locks and explicit SQL; master data benefits from JPA |
| 5 | Identifiers | UUIDv7 primary keys; short human references from database sequences | Offline devices create ids; time-ordered ids index well |
| 6 | Money and volume | `long` piastres and `long` millilitres everywhere, `BIGINT` with `CHECK` constraints | Same model as the web; no floating point anywhere |
| 7 | Time | `timestamptz` in UTC, `Instant` in code, an injected `Clock`; limits evaluated in the station's time zone | PRD §2.3; makes time testable |
| 8 | Authentication | Spring Security. Argon2id passwords. 15-minute JWT access token. Opaque refresh token, stored hashed, rotated, in an httpOnly cookie. Sessions in PostgreSQL. | Proven in Nagahny; revocation takes effect immediately |
| 9 | MFA | TOTP for Ops and Super Admin from B1; station financial roles in B6 | PRD §10.2, roadmap delta A3 |
| 10 | Device channel | Single-use activation code → device key pair → device tokens with `typ=device` → every batch signed with a timestamp and nonce | PRD device binding; avoids the shared-key weakness found in Nagahny |
| 11 | Background work | `@Scheduled` with a database lock for periodic jobs; Modulith event outbox for after-commit work | No extra infrastructure; survives restarts |
| 12 | Rate limiting | Per user or device, per IP only when anonymous; Redis-backed when deployed | Egyptian carriers put many users behind one IP |
| 13 | Tests without Docker | Real PostgreSQL through embedded binaries (Zonky), locally and in CI | RLS, locks and constraints cannot be tested on H2 |
| 14 | Deployment | Railway, new environment `zawed-dev` (API, PostgreSQL, Redis, web). Flyway runs before deploy. | `zood-web` stays frozen and untouched |
| 15 | Base package | `com.zawed` | Cheap to choose now, expensive to rename later |

---

## 5. What we carry over from Nagahny and Soker

### 5.1 Patterns to reuse

| Pattern | Where it lives | How ZAWED uses it |
|---|---|---|
| Refresh rotation with reuse detection, a 60-second grace window, role and status re-read on every refresh | `darsly/apps/api/src/auth/token.service.ts` | Same design in `identity` |
| Session checked on every request, so logout and bans are immediate | `darsly/.../common/guards/jwt-auth.guard.ts` | A security filter that loads the session row |
| Refresh token in an httpOnly, path-scoped cookie | `darsly/.../auth/session-cookie.ts` | From day one |
| Constant-time login (verify against a dummy hash), lockout counters | `darsly/.../auth/auth.service.ts` | Users: lockout. Agents: lock after 5 failures, Ops unlocks (PRD §6.1) |
| Fail-fast configuration check at boot | `darsly/.../common/config.validation.ts` | Validated `@ConfigurationProperties`; refuse to start on weak or placeholder secrets |
| Deny by default, with an explicit public marker | `darsly` global guards | `anyRequest().authenticated()` plus method-level permissions |
| Admin-minted device code: single use, hashed, 15 minutes, attempt cap | `darsly/.../device/device-enrollment.service.ts` | POS device activation (PRD §6.3) |
| Device tokens that can never pass as user tokens (`typ: 'device'`) | `darsly/.../device/device-token.service.ts` | `/api/v1/pos/*` accepts only device tokens |
| Double-entry ledger with named accounts and immutable entries | `darsly/.../payments/ledger.service.ts` | `ledger` module (§3.5) |
| Conditional state flip (`update … where status = 'PENDING'`, check the row count) | `darsly/.../payouts/payouts.service.ts` | Top-ups, payouts, reversal approvals |
| Unique key as the final guard against double posting | `darsly` ledger (`paymentId`, `payoutId`) | `(source_type, source_id)` and `client_txn_id` |
| Error contract with stable codes, and a CI check that every code has Arabic and English copy | `darsly/.../common/errors/api-error.ts`, `scripts/check-error-codes.mjs` | Same envelope: `code, message, field, params, retryable, requestId` |
| Request id on every log line and response | `darsly/.../common/request-context.ts` | A filter with MDC, JSON logs |
| Liveness separate from readiness | `darsly/.../health/health.controller.ts` | Actuator probes; Railway checks readiness |
| Per-user throttling | `darsly/.../common/guards/user-throttler.guard.ts` | ADR 12 |
| Adversarial scripts for IDOR, payment replay and concurrent withdrawals | `darsly/scripts/audit-*.mjs`, `concurrency-wallet.mjs` | Become automated tests in B4 and B7 |
| One permission helper enforced on every mutation | `soker/src/lib/authz.ts` | One permission matrix, one enforcement point |
| Audit entry written in the same transaction, actor taken from the session | `soker/src/lib/activity.ts` | `audit` module; `created_by` and `updated_by` on every record |
| Forced password change for temporary credentials | `soker/src/middleware.ts` | Ops-issued Fleet Manager credentials and agent PINs |
| Gap-free sequential numbers with `UPDATE … RETURNING` | `soker/src/lib/invoice.ts` | Receipt references, later invoice numbers |
| Scheduled off-site backups, a restore test, a written DR guide | `soker/.github/workflows/db-backup.yml`, `scripts/backup/*`, `docs/disaster-recovery-guide.md` | B7, sized to the PRD targets (RPO 1 hour, RTO 4 hours) |
| A self-test and a written summary at the end of each phase | `soker/scripts/selftest-*.ts`, `docs/summaries/` | Integration tests per phase and `docs/summaries/` |

### 5.2 Mistakes not to repeat

| What went wrong | Source | ZAWED rule |
|---|---|---|
| No CI for a long time; broken code reached `main` | Nagahny review #1 | CI exists before the first feature (B0) |
| 174 unbounded list queries | Nagahny review #5 | Every list endpoint is paginated, with one shared page envelope |
| Refresh tokens in `localStorage` | Nagahny review #9 | Never; cookie only |
| A static shared key shipped inside the Android app | Nagahny review #11 | Per-device keys and signed requests (ADR 10) |
| Forgot-password revealed which e-mails exist | Nagahny review #10 | Identical answer for every input |
| Soft-delete hidden in ORM middleware, with silent gaps | Nagahny review #12 | Explicit status columns (PRD §8.2); no hidden filter |
| Fire-and-forget background work lost on deploy | Nagahny review #2, #6 | Outbox and leased jobs; graceful shutdown |
| No request id or structured logs at first | Nagahny review #19 | Present from B0 |
| Missing indexes on foreign keys | Nagahny review #15 | Every foreign key is indexed in its migration |
| Decimal money converted to floating point in reports | Soker audit H1, M1, M5 | `long` minor units end to end, including exports |
| Balance chain recomputed without a row lock | Soker audit S2 | Balance changes only under `SELECT … FOR UPDATE` |
| Full chain recompute on every write | Soker audit P1 | Append-only postings with a running balance; no recompute |
| No login rate limit | Soker audit L6 | Rate limit and lockout from B1 |
| Uploads with no size or type check | Soker audit H2, H3; Nagahny review #26 | Size caps and magic-byte checks when evidence upload arrives |

### 5.3 If you know NestJS and Prisma, this is the Spring equivalent

| NestJS / Prisma | Spring Boot |
|---|---|
| Module | Modulith module (a package with a public API) |
| Guard, `@Roles` | `SecurityFilterChain`, `@PreAuthorize` |
| `ValidationPipe`, class-validator | Bean Validation (`@Valid`) on record DTOs |
| `prisma.$transaction` | `@Transactional` |
| `schema.prisma` + `migrate` | Flyway SQL files; entities validated against the schema |
| `updateMany` + count check | `@Modifying` query returning the affected row count |
| Exception filter | `@RestControllerAdvice` |
| `ThrottlerGuard` | Bucket4j filter |
| Swagger module | springdoc-openapi |
| Jest | JUnit 5 + AssertJ |
| `validateConfig()` | `@ConfigurationProperties` + `@Validated` |

---

## 6. Security baseline

Required from B1 onward, on every endpoint:

1. Authentication on everything except an explicit public list.
2. Permission check on the server for every action; the UI hiding a button is not a control.
3. Scope from the token only; 404 for anything outside it; RLS underneath.
4. Bean Validation on every input; unknown fields rejected.
5. Rate limits on login, PIN entry, refresh, activation, fueling and exports.
6. Audit entry for every privileged change, in the same transaction.
7. Secrets from the environment only; boot fails on weak values.
8. TLS everywhere, strict CORS allow-list, security headers, no stack traces in responses.
9. Tag UIDs stored encrypted with a keyed hash for lookup, and shown masked.
10. Dependency scanning in CI.

PRD §10 threats and where each control lands:

| Threat | Control | Phase |
|---|---|---|
| Concurrent overdraft, duplicate deduction | Wallet row lock, unique `clientTxnId`, unique ledger source | B3, B4 |
| Rapid multi-station fueling | Cooldown on the tag, mirrored centrally | B4 (central), POS track (tag write) |
| Stolen tag | Instant deactivation, status checked on every scan | B2, B4 |
| Offline overspend | Provisional local decrement, conflict detection at sync | B5 |
| Unauthorised or stolen POS device | Device binding, signed requests, instant revocation | B5 |
| Worker credential sharing | Single active session per Worker ID | B5 |
| Tank capacity exploitation | Non-blocking alert to Ops | B4 |
| Stale price | Effective-dated schedules sent ahead of time; flag beyond the staleness window | B2, B5 |
| Internal admin fraud | Append-only audit, two-person reversal, dual control on payouts and bank changes | B1, B6 |
| Printer manipulation | Digital receipt independent of the printer | B4 |

---

## 7. Phases

Names are B0–B8 to avoid confusion with PRD Phases 0–4 and demo phases D0–D8. Sizes are rough estimates for one developer working with Claude, not commitments.

### B0 — Foundations (about 1 week)

- **Objective:** an empty but production-shaped system: it builds, tests, deploys and answers one request end to end.
- **Deliverables:**
  - ADRs for §4;
  - `apps/api` skeleton with the `platform` module (Money, Volume, ids, clock, error envelope, request id, JSON logs, health probes, configuration check);
  - Flyway baseline migration;
  - embedded-PostgreSQL test setup and the Modulith verification test;
  - `money.test.ts` ported to Java as golden tests;
  - CI for web and API;
  - Railway `zawed-dev` with API and PostgreSQL;
  - OpenAPI document, generated TypeScript types, `http-api.ts` and the `VITE_API_MODE` switch.
- **Exit:** CI is green; the dev web app shows data served by the deployed API; the demo still passes its E2E tests in mock mode.

### B1 — Identity and access (about 2 weeks)

- **Objective:** real users, sessions and permissions for the three web portals.
- **Deliverables:**
  - users and roles (Super Admin, Ops, Fleet Manager, Accountant, Station Manager);
  - login, refresh, logout, lockout, forced password change, password reset;
  - TOTP for Ops and Super Admin;
  - the permission matrix (PRD §3) as code;
  - tenant context and RLS;
  - audit log;
  - rate limiting;
  - web login screens and route guards.
- **Exit:** an automated role × endpoint matrix test passes; a cross-tenant request answers 404 in both the API test and the RLS test.

### B2 — Master data and Ops onboarding (about 2 weeks)

- **Objective:** Ops can set up the real world.
- **Deliverables:**
  - companies and departments;
  - vehicles;
  - tags with their lifecycle (Active, Lost, Stolen, Deactivated, Replacement Pending);
  - station operators and stations;
  - fuel price schedules with effective dates;
  - POS device registration, activation code and revocation;
  - agents with Worker ID and PIN;
  - limit rules;
  - Ops portal write screens; Company and Station portals reading real data;
  - a seed script that reproduces the demo dataset.
- **Exit:** the demo dataset can be created through the API alone; every list is paginated; every change is audited.

### B3 — Ledger and wallet (about 1.5 weeks)

- **Objective:** money can enter the system and be accounted for.
- **Deliverables:**
  - the ledger module (§3.5);
  - company wallets;
  - the top-up workflow (request, Ops credit with a bank reference, duplicate-reference guard);
  - wallet statement;
  - low-balance alert;
  - the reconciliation job and its Ops view.
- **Exit:** a concurrency test with parallel credits and debits ends with ledger and cached balance equal; the database rejects an unbalanced ledger transaction and any update to an entry.

### B4 — Transaction engine, online (about 2 weeks)

- **Objective:** a real fueling, end to end, from the web POS simulator.
- **Deliverables:**
  - the `rules` module ported from `fueling.ts` and `pos.ts`, with their tests ported as golden tests;
  - the fueling endpoint (§3.4);
  - limits and cooldown;
  - hard alerts;
  - digital receipt with a signed link;
  - live feed over server-sent events;
  - the POS simulator in online mode.
- **Exit:** the investor-demo sequence steps 1–4 run against the real backend; 50 parallel fuelings on one wallet never overdraw and never double-charge; replaying a `clientTxnId` returns the original result; p95 response time is measured against the 2-second target.

### B5 — Device channel and offline sync (about 2–3 weeks)

- **Objective:** the offline protocol, proven before any Android code exists.
- **Deliverables:**
  - device activation, tokens and request signing;
  - agent login with single active session;
  - the snapshot endpoint (vehicles, tags, limits, wallet status, price schedules, agents);
  - the batch ingest endpoint (idempotent, committed with conflict alerts, cooldown reconciliation);
  - contract-version check;
  - the sync monitor;
  - the POS simulator using the real snapshot and a persistent queue.
- **Exit:** demo sequence steps 5–6 run against the real backend; a batch sent twice changes nothing the second time; a revoked device is refused on its next call.

### B6 — Station finance and operations (about 3 weeks, needs the roadmap delta approved)

- **Objective:** stations get paid and everyone can reconcile.
- **Deliverables:**
  - station payable postings;
  - payouts with bank reference and dual control above a threshold;
  - the station statement with drill-down;
  - bank-detail change control with a payout hold;
  - MFA for station financial roles;
  - two-person reversals;
  - tickets and requests;
  - alert queue actions;
  - asynchronous exports (XLSX, CSV);
  - e-mail and in-app notifications.
- **Exit:** for a test month, company statements, station statements and the ledger agree to the piastre.

### B7 — Hardening and pilot readiness (about 2 weeks)

- **Objective:** safe to run with real money.
- **Deliverables:**
  - automated IDOR sweep and the full authorisation matrix;
  - load and concurrency tests;
  - backups with point-in-time recovery and a rehearsed restore;
  - metrics, error reporting and alerting;
  - retention jobs;
  - staging and an isolated test tenant;
  - runbooks;
  - an external security review.
- **Exit:** a restore rehearsal meets RPO 1 hour and RTO 4 hours; no open high-severity finding.

### B8 — Kotlin POS (parallel track, starts after the hardware spike)

- **Objective:** the real terminal.
- **Steps:**
  1. Run the hardware spike (`docs/architecture/hardware-spike.md`).
  2. Record the two ADRs (tag model, POS framework).
  3. Build the Kotlin app against the B5 protocol, reusing the `rules` module.
- **Not blocked by:** B0–B4. The spike can start now; it needs devices and tags, not code.

### Order and dependencies

```
B0 → B1 → B2 → B3 → B4 → B5 → B6 → B7 → pilot
                                ↘
Hardware spike (start now) ──────→ B8 Kotlin POS → pilot
Legal and commercial decisions (start now) ──────→ needed by B6
```

---

## 8. First steps (B0, in order)

| # | Step | Result |
|---|---|---|
| 1 | Approve the decisions in §12 | The plan becomes the baseline |
| 2 | Commit this plan, `docs/competitive-analysis/` and the ADRs on `develop` | Documents are versioned |
| 3 | ~~Install PostgreSQL locally~~ Not needed: an embedded PostgreSQL 18 runs tests and local development (ADR 0013) | A local database without Docker or an install |
| 4 | Scaffold `apps/api` with the Maven wrapper and the `platform` module | `mvnw verify` passes; `/api/v1/health` answers |
| 5 | Add Flyway, the embedded-PostgreSQL test setup and the Modulith test | Migrations run in tests |
| 6 | Port `money.ts` and its tests to Java | The arithmetic is identical on both sides |
| 7 | Add `.github/workflows/ci.yml` (web `npm run check` and build; API `mvnw verify`) | Every push is checked |
| 8 | Create the Railway `zawed-dev` environment (API, PostgreSQL); pre-deploy migration; readiness healthcheck | A deployed dev API |
| 9 | Publish the OpenAPI document; generate TypeScript types; add `http-api.ts` and `VITE_API_MODE` | The web can talk to the API |
| 10 | Write `docs/summaries/B0.md` and ask for approval to start B1 | Controlled hand-over |

Each later phase follows the same loop: state the objective, files and assumptions → build → tests → summary → approval.

**Definition of done for every phase**

- Unit, integration and E2E tests are green in CI.
- New endpoints are in the authorisation matrix test.
- Migrations are forward-only and applied on `zawed-dev`.
- OpenAPI types are regenerated; the web compiles against them.
- New UI text exists in Arabic and English.
- The phase summary is written.

---

## 9. Git and environments

- All work on `develop` or `feature/*` branches. `master` and Railway `zood-web` stay frozen.
- New Railway services live in a separate environment (`zawed-dev`). Nothing is deployed to `zood-web`.
- Merging to `master` happens only on an explicit instruction, after the full regression.

---

## 10. Work that is not engineering (start now)

These block the pilot, and code cannot resolve them (roadmap delta §14):

| # | Question | Blocks |
|---|---|---|
| 1 | Legal entity and licensing for holding prepaid fleet funds | Pilot |
| 2 | Who issues fuel invoices; e-invoice obligations | B6 documents |
| 3 | Settlement contract: payout frequency, fee placement | B6 |
| 4 | Who pays for VIP fuel; who bears offline negative balances | VIP, B5 conflict policy |
| 5 | Are pump prices nationally uniform for all pilot fuel types | B2 price schedules |
| 6 | Hardware procurement: two Sunmi P2 devices, tag samples | B8 |
| 7 | Pilot station operator and two to five fleet companies | Pilot |
| 8 | Hosting and data-residency requirements for financial data in Egypt | B7 hosting choice |

---

## 11. Risks

| Risk | Mitigation |
|---|---|
| First Spring project for the team | B0 and B1 are small on purpose; §5.3 maps known concepts; Modulith tests catch structural mistakes early |
| The offline protocol is designed before real hardware exists | The web simulator proves the protocol in B5; the spike runs in parallel and can change tag and device details without touching the engine |
| RLS mistakes are silent | Dedicated cross-tenant tests in B1; the runtime role cannot bypass RLS |
| PostgreSQL point-in-time recovery may not be available on the chosen host | Decide hosting in B7 against the RPO target before the pilot |
| Scope growth from the roadmap delta | The delta's V1 items stay out until the pilot runs |

---

## 12. Decisions needed from the product owner

1. **Scope baseline:** approve the roadmap delta's Pilot MVP (§2), or stay with PRD Phase 1 as written.
2. **Stack:** approve ADRs 1–15 (§4), in particular Java 25, Maven, JPA plus plain SQL, and tests on embedded PostgreSQL.
3. **Environments:** approve a new Railway environment `zawed-dev` and a CI workflow on GitHub.
4. **Order:** approve B0 → B1 as the first two phases, with the hardware spike and the legal questions running in parallel.
5. **VIP:** confirm it is modelled but switched off for the pilot.
