# Security review pack

For the external security reviewer (B7). It covers what ZAWED is, what to test, how to get access, and what we already know is weak. Each claim cites the decision record (ADR) or the code behind it, so you can check it rather than take it on trust.

## 1. The system in one paragraph

ZAWED (زوّد) is a B2B fleet fuel platform for Egypt.
- **Who pays:** fleet companies prepay a wallet.
- **Where they fuel:** their vehicles fuel at partner stations. A POS terminal reads the vehicle's tag, checks limits and balance, and records the fueling.
- **How money moves:** the company's wallet is debited, and the station's payable is credited in a double-entry ledger. Stations are paid out from their payables.

The system has three parts:
- an API: Spring Boot 4, Java 25, PostgreSQL 18, a modular monolith, `apps/api`;
- a web app with the company, station and Ops portals, plus a POS simulator: React, `apps/web`;
- a native POS: Kotlin. It is out of this scope and built separately against the POS protocol below.

**Further reading:**
- the architecture: `docs/architecture/implementation-plan.md`, the module tree in `docs/architecture/diagrams/`;
- the database and tenancy: `docs/architecture/database-erd.md`;
- every decision: `docs/adr/`.

## 2. Scope and rules of engagement

| In scope | Out of scope |
|---|---|
| The API on the dev environment: `https://zawed-api-production.up.railway.app/api/v1` (OpenAPI at `/api/v1/openapi.json`, interactive at `/api/v1/docs`) | The investor demo (`zood-web`, branch `master`): never touch it |
| The web app on the dev environment: `https://zawed-web-production.up.railway.app` | The Railway platform itself; denial-of-service and volumetric load |
| The source in this repository, at the commit you are given | Social engineering; the owner's personal accounts |

- Use only the test tenant and the accounts we create for you. Do not create real companies or real money flows.
- Load: at most 10 requests per second sustained. Rate limits exist (ADR 0012) and may block you; tell us, and we will lift them for your IPs.
- Report anything that exposes another tenant's data or moves money **at once** (SEV1, `docs/runbooks/incident.md`), before continuing.

## 3. Access we give you

You get one account of each role in the test tenant:
- Super Admin;
- Ops and Ops Manager;
- for a test company: fleet manager and accountant;
- for a test operator: station manager;
- one activated POS device and two agents.

**A second test company and operator** are there so you can try to cross between tenants.

**MFA:** platform roles and station financial roles must enrol an authenticator (ADR 0009). We hand over the enrolment, not a shared code.

## 4. The controls, and where they live

| Area | Control | Where |
|---|---|---|
| **Sign-in** | Argon2id passwords, 12–128 characters; the same answer and time for unknown email, wrong password and wrong door; five failures lock the account for 15 minutes | ADR 0008, `identity/SignIn.java`, `platform/crypto/PasswordHasher.java` |
| **Sessions** | Short-lived access tokens; rotating refresh tokens in an HttpOnly cookie, where reusing an old refresh token revokes the whole session; an origin check on refresh and logout | `identity/Sessions.java`, `identity/AuthCookies.java` |
| **MFA** | TOTP for Super Admin, Ops, Ops Manager and station managers; each time step is accepted once | ADR 0009, `identity/Totp.java` |
| **Tenant isolation** | Two walls:<br>1. **PostgreSQL row-level security** on every tenant table. The runtime role `zawed_app` cannot bypass it; every transaction sets `app.scope_type`, `app.scope_id` and `app.user_id`, and a transaction without them sees nothing.<br>2. **Scope checks in the API.** A forbidden row answers 404. | `database-erd.md` (multi-tenancy), `V3__runtime_role_and_rls.sql`, `platform/security/ScopedTransactionManager.java` |
| **Platform-scope escalations** | The few places that run as the platform (the POS chain, workers) are named and listed in a test | `ArchitectureTests` |
| **Authorisation** | A matrix test of role × endpoint | `identity/AuthorizationMatrixTests.java` |
| **POS devices** | Activation with a one-time code; the device registers its own P-256 public key and signs a timestamp and nonce for every token. No shared secret ships in the app. Nonces are kept against replay. Every request checks that the device and its operator are active. | ADR 0010, ADR 0024, `network/DeviceChannel.java`, `network/PosCredentials.java` |
| **Offline fuelings** | A snapshot without tag UIDs (per-device digests) and without balances (a capped allowance); every capture is recorded at sync, with conflicts and alerts | ADR 0019, ADR 0025, `sync/Snapshots.java`, `fueling/OfflineSync.java` |
| **Money** | Double-entry postings, balanced and checked again at commit; one posting per source; wallet balances change only under the wallet's row lock; lock order wallet → vehicle → tag → payable | `ledger/Ledger.java`, ADR 0017, ADR 0018 |
| **Dual control** | Top-ups: verified against the bank, then approved by another person. Wallet adjustments, payouts above a threshold, bank-detail changes and reversals: requested by one person, decided by another. Enforced in the database too, not only the service. | ADR 0020, ADR 0021, V9, V12 |
| **Idempotency** | `Idempotency-Key` on money-moving requests; the same key with a different body is refused | ADR 0022, V13 |
| **Data at rest** | MFA secrets, bank account numbers and tag UIDs are encrypted with AES-256-GCM, bound to their row | `platform/crypto/DataEncryptor.java` |
| **Uploads** | CSV/XLSX import: 5 MB and 5,000 rows at most; zip bombs refused; a DOCTYPE or ENTITY anywhere refused; formula cells defused in exported reports | ADR 0026, `fleet/FleetImportFile.java` |
| **Audit** | Append-only, written in the same transaction as the change; the runtime role cannot update or delete it | `audit/AuditLog.java`, V3 |
| **Error reporting** | Sentry, without request data, users or database messages | `platform/observability/SentryScrubber.java`, `apps/web/src/lib/observability.ts` |

## 5. What we most want you to try

1. **Read or change another tenant's data.** Try every endpoint that takes an id, from every role, with ids of the other company or operator. Try lists, exports, the calendar and imports too.
2. **Create or move money alone.** Top-ups, adjustments, payouts, reversals. Try replaying, racing and reordering requests, approving your own request, and changing an amount between steps.
3. **Fake a POS:**
   - fuel without a valid device signature;
   - replay a signed request;
   - use a revoked device;
   - tamper with the offline snapshot, or sync captures that never happened.
4. **Take over a session:**
   - refresh token theft and reuse;
   - CSRF on cookie endpoints;
   - MFA bypass;
   - account enumeration through timing.
5. **Abuse an upload:** a malicious XLSX or CSV (XXE, zip bomb, formula injection, encodings).

## 6. Known limitations (no need to report these)

| Limitation | Status |
|---|---|
| `ZAWED_DATA_KEY` cannot be rotated: only one key is implemented | B7 W9, in progress |
| No automated IDOR sweep yet; the matrix test covers roles × endpoints, not every id | B7 W2, in progress |
| Offline exposure across devices is monitored, not capped | Owner decision pending (ADR 0025) |
| Backups are manual `pg_dump`; no point-in-time recovery | Deferred to the pilot host (`docs/runbooks/backup-and-restore.md`) |
| Swagger UI is public on dev | On purpose for dev; off in production (`ZAWED_API_DOCS=false`) |
| Review findings of 2026-11-19 | Being fixed: `docs/artifacts/production-hardening/final-handover.md`, limitation 1 |

## 7. How to report

For each finding, give:
- a title;
- the severity (CVSS, or Critical/High/Medium/Low);
- the affected endpoint, screen or file;
- the steps to reproduce, with requests;
- the impact in our terms: which tenant, which money, which data;
- your suggested fix.

Report SEV1 findings at once, the rest at the end.
