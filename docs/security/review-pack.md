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
| **Sign-in** | Argon2id passwords, 12–128 characters; one sign-in path for every role since B7 W10 (ADR 0008 amendment); the same answer and time for unknown email and wrong password, whatever the role; five failures lock the account for 15 minutes | ADR 0008, `identity/SignIn.java`, `platform/crypto/PasswordHasher.java` |
| **Request paths** | Every path must arrive in its one canonical spelling: segments of unreserved characters only, with no percent-encoding, `;` matrix parameter, empty, `.` or `..` segment. Anything else answers `400 PATH_NOT_CANONICAL` before the security chain runs. Our own filters (sign-in and refresh limits, the password-change gate, the POS public paths) match with Spring Security's `PathPatternRequestMatcher`, the same matcher as `permitAll` and the routing, never with a raw string. A test lists every class that reads the raw path. Fixed in B7 after an independent review (High, 2026-11-20): `POST /api/v1/auth/logi%6E` reached the sign-in step without the per-address sign-in limit, and the same was true of the MFA, enrolment, setup and refresh steps | `platform/web/CanonicalPathFilter.java`, `platform/security/SecurityFilters.java`, `identity/PathMatchingTests.java`, `ArchitectureTests` |
| **Sessions** | Short-lived access tokens; rotating refresh tokens in an HttpOnly cookie, where reusing an old refresh token revokes the whole session; an origin check on refresh and logout | `identity/Sessions.java`, `identity/AuthCookies.java` |
| **MFA** | TOTP for Super Admin, Ops, Ops Manager and station managers; each time step is accepted once | ADR 0009, `identity/Totp.java` |
| **Tenant isolation** | Two walls:<br>1. **PostgreSQL row-level security** on every tenant table. The runtime role `zawed_app` cannot bypass it; every transaction sets `app.scope_type`, `app.scope_id` and `app.user_id`, and a transaction without them sees nothing.<br>2. **Scope checks in the API.** A forbidden row answers 404. | `database-erd.md` (multi-tenancy), `V3__runtime_role_and_rls.sql`, `platform/security/ScopedTransactionManager.java` |
| **Platform-scope escalations** | The few places that run as the platform (the POS chain, workers) are named and listed in a test | `ArchitectureTests` |
| **Authorisation** | A matrix test of role × endpoint, and an IDOR sweep of every endpoint × scoped role × the other tenant's ids. The permission is checked before the request is read: a caller without it gets `403 FORBIDDEN` before the body is parsed or validated and before path ids are converted, and before a multipart upload is parsed (`resolve-lazily`), so a refusal never names a field. Fixed after QA round 1 (2026-11-21): a station token on `POST /api/v1/ops/stations/{id}/payouts` got 400 with the payout's field names, because method security ran after bean validation. A permission shared by two portals (`TICKETS_OPEN`) also names the scope type it accepts on each portal's writes | `platform/security/PermissionFirst.java`, `identity/AuthorizationMatrixTests.java` (`aRoleWithoutThePermissionIsRefusedBeforeTheBodyIsRead`, `…BeforeAnUploadIsParsed`), `fueling/IdorSweepTests.java` |
| **Input** | Unknown JSON fields are refused. Money (integer piastres) and volume (integer millilitres) refuse a fractional number (`100.5`, also `100.0` and `1e2`) with `400 VALIDATION_FAILED`, code `INTEGER_REQUIRED` on the field, instead of truncating it (QA round 1). Company and operator contact phones must be Egyptian mobiles or landlines, tax ids nine digits, commercial registrations 3 to 12 digits, agent phones Egyptian mobiles; each is cleaned (Arabic digits, separators, `+20`/`0020`) and stored in that form. Rows written before the rule are read as they are | `application.yaml` (`accept-float-as-int`), `platform/web/GlobalExceptionHandler.java`, `platform/text/EgyptianContact.java`, `fueling/WholeNumberTests.java`, `tenancy/CompanyContactTests.java` |
| **Staff names** | Maker-checker records (top-ups, adjustments, bank changes, payouts) carry the names of who asked and who decided. A company or station sees its own users' names; ZAWED staff appear to it only as the role `ZAWED_OPERATIONS`, never by name or e-mail. Checked in code and by row-level security on `users` | `identity/ActorDirectory.java`, `ledger/MakerCheckerNamesTests.java`, `fueling/StationFinanceTests.java` |
| **POS devices** | Activation with a one-time code; the device registers its own P-256 public key and signs a timestamp and nonce for every token. No shared secret ships in the app. Nonces are kept against replay. Every request checks that the device and its operator are active. | ADR 0010, ADR 0024, `network/DeviceChannel.java`, `network/PosCredentials.java` |
| **Offline fuelings** | A snapshot without tag UIDs (per-device digests) and without balances (a capped allowance); every capture is recorded at sync, with conflicts and alerts | ADR 0019, ADR 0025, `sync/Snapshots.java`, `fueling/OfflineSync.java` |
| **Money** | Double-entry postings, balanced and checked again at commit; one posting per source; wallet balances change only under the wallet's row lock; lock order wallet → vehicle → tag → payable | `ledger/Ledger.java`, ADR 0017, ADR 0018 |
| **Dual control** | Top-ups: verified against the bank, then approved by another person. Wallet adjustments, payouts above a threshold, bank-detail changes and reversals: requested by one person, decided by another. Enforced in the database too, not only the service. | ADR 0020, ADR 0021, V9, V12 |
| **Idempotency** | `Idempotency-Key` on money-moving requests; the same key with a different body is refused | ADR 0022, V13 |
| **Data at rest** | MFA secrets, bank account numbers and tag UIDs are encrypted with AES-256-GCM, bound to their row. The key rotates without downtime: each value names its key, old values are re-encrypted in the background, and the database lets a stored value change only into a re-encryption under a higher key id | `platform/crypto/DataEncryptor.java`, `platform/crypto/DataKeyRotation.java`, ADR 0027, V20 |
| **Uploads** | CSV/XLSX import: 5 MB and 5,000 rows at most; zip bombs refused; a DOCTYPE or ENTITY anywhere refused; formula cells defused in exported reports | ADR 0026, `fleet/FleetImportFile.java` |
| **Audit** | Append-only, written in the same transaction as the change; the runtime role cannot update or delete it | `audit/AuditLog.java`, V3 |
| **Public demo form** (U1) | `POST /api/v1/public/demo-requests`, the only anonymous write that stores personal data. A 4 KiB body cap; limits shared across instances and deploys in PostgreSQL (5 an hour and 20 a day per address, IPv6 by /64 and 20 an hour per /48, gated on the decoded path; 3 a day per phone and per e-mail and 60 an hour for the whole form, past which requests are set aside silently); a honeypot and a time trap answered 202 with no row; repeats within 24 h counted on the same lead; always the same 202. Rows go in only through a `SECURITY DEFINER` function; only platform users can read them (RLS); nothing personal is logged | `u1-leads-contract.md`, ADR 0012 (amended), V22, `leads/`, `platform/ratelimit/PostgresRateLimiter.java` |
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
   - account enumeration through timing;
   - another spelling of a path (encoding, case, slashes, dots, `;`) that reaches a controller without its limit or check.
5. **Abuse an upload:** a malicious XLSX or CSV (XXE, zip bomb, formula injection, encodings).
6. **Abuse the public demo form:**
   - get around its limits with forged `X-Forwarded-For`, IPv6 rotation, concurrency, or by waiting for a deploy;
   - learn whether a phone or e-mail is already a lead;
   - store control or bidi characters that would mislead the Ops console;
   - read or change a lead from a company, station or POS session.

## 6. Known limitations (no need to report these)

| Limitation | Status |
|---|---|
| The lookup key (tag UID hashes, receipt links, device digest keys) does not rotate with the data key; after a leak of the original data key it stays usable against a copy of the database (tag UIDs as short as 8 hex digits can be brute-forced from it) | Owner decision pending (ADR 0027) |
| During a data-key rotation window, a holder of the current key with a runtime session can overwrite a bank account or tag UID not yet re-encrypted; the swap is audited (`BANK_ACCOUNT_REKEYED`) but not detected, and a swapped tag never reaches a device | Accepted, short window; verify accounts against `account_last4` before retiring the old key (ADR 0027) |
| A top-up request answers `409 BANK_REFERENCE_IN_USE` for a reference another company claimed. It reveals one bit, and the first claim wins, so a company that guesses another's reference first can block the genuine claim; verification does not match the payer | Owner decision pending (`known-issues.md`) |
| Offline exposure across devices is monitored, not capped | Owner decision pending (ADR 0025) |
| Backups are manual `pg_dump`; no point-in-time recovery | Deferred to the pilot host (`docs/runbooks/backup-and-restore.md`) |
| The demo form trusts the browser's clock for its time trap (`startedAt`): a visitor whose clock runs far ahead is silently dropped, and a script can set any value | Known; a cheap filter, not a control (`u1-leads-contract.md` §9) |
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
