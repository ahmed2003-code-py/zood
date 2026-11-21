# ADR 0029: The backend on ASP.NET Core

**Status:** Accepted, 2026-11-21. The move is the owner's decision; the design below is phase 0 of the migration (`docs/architecture/dotnet-migration-plan.md`).
**Source:** owner decision of 2026-11-21 (the new responsible developer works in .NET); `docs/dotnet/dotnet-backend-playbook/` (his house conventions); every earlier ADR.
**Supersedes at the cutover:** ADR 0001 (Java), 0002 (Maven), 0003 (Spring Boot and Spring Modulith), 0013 (embedded PostgreSQL for tests). **Amends at the cutover:** 0004 (EF Core and Dapper instead of JPA and `JdbcClient`), 0011 (hosted services instead of `@Scheduled`; in-transaction events instead of the Modulith outbox), 0015 (namespaces instead of the base package). Until zawed-dev runs the .NET API, those ADRs still govern `apps/api`.

## Context

- The API is Spring Boot 4 on Java 25: about 197 classes in 15 modules, about 90 test classes, 25 Flyway migrations (V1 to V23, with V6_1, V7_1 and V8_1; V18 never used).
- There are no real users or data yet. `zawed-dev` holds test data at schema 23.
- Two clients depend on the HTTP contract and must not notice the change:
  - the web app (`apps/web`), whose TypeScript types are generated from `apps/api/openapi/openapi.json`;
  - the native Kotlin POS, built by another developer from the B4/B5 contracts.
- The owner's decisions:
  - keep PostgreSQL;
  - keep the same HTTP contract;
  - switch zawed-dev to the .NET API only once it has full parity, and only after the owner has tested it himself (no automatic cutover).
- Everything the earlier ADRs decided must survive the move:
  - the money invariants (`.claude/agents/zawed-deep.md`);
  - row-level security as the tenant boundary;
  - the sign-in and device protections;
  - the encrypted columns;
  - the job records.

## Decision

### 1. Solution shape: a modular monolith in one web project, a pure domain project, one test project

```
apps/api-dotnet/
  Zawed.slnx, Directory.Build.props, Directory.Packages.props (one version per package), Dockerfile
  db/migration/V*.sql                 the Flyway files, byte for byte (the schema source)
  src/Zawed.Domain/                   pure: Money, Volume, UuidV7, FuelingRules, Periods (no package references)
  src/Zawed.Api/
    Program.cs                        five lines; everything is in Platform/PlatformSetup.cs
    Platform/                         the shared kernel (the Java `platform` package)
      Config/ Web/ Security/ Persistence/ Persistence/Migrations/ Crypto/ Jobs/ Health/ Meta/ OpenApi/ Events/ Modules/
    Modules/<Name>/                   one folder per Java module: identity, tenancy, fleet, network, pricing,
                                      ledger, fueling, sync, stationfinance, tickets, notifications, leads, audit
      Contracts/                      the module's public surface: I<Name>Api interfaces, events, shared records
      <Name>Module.cs                 IZawedModule: registers the module's services, stores and jobs
      *Controller.cs, services, stores (SQL), EF configurations, DTO records
  tests/Zawed.Tests/                  xUnit, Testcontainers PostgreSQL 18, WebApplicationFactory
```

**Why not the playbook's 3-tier (`Api / Core / EF`).**
- The playbook keeps 3-tier for its default case and names the modular monolith (Shifa) for "large platforms with many modules and a small team". ZAWED is that case: 15 modules whose boundaries are already enforced.
- A Java module maps to one folder. Each module agent then owns one folder and can be reviewed side by side with the Java package it ports.
- 3-tier would split every module across three projects. About 40 SQL stores would also need an interface in `Core` and an implementation in `EF`, each with exactly one implementation.
- The money path needs explicit SQL and locks, not repositories (§2).
- The playbook allows this shape explicitly: the `DbContext` is the Unit of Work and each `DbSet` its repository. A module that grows can still move to its own project, because its boundary is already drawn.

**Boundaries are enforced on the compiled code.** `ArchitectureTests` reads the IL with Mono.Cecil and replaces Spring Modulith and the Java `ArchitectureTests`:
- a module reaches another only through `Modules/<Other>/Contracts`;
- module dependencies have no cycle;
- `Platform` depends on no module;
- `Zawed.Domain` references nothing but the base library. A separate project makes that a compile-time fact, like the Java rule "`rules` has no framework dependency".
- Reviewed lists that grow only with a review:
  - scope escalations;
  - endpoints that need a signed-in user but no permission;
  - readers of the raw request target;
  - readers of the system clock;
  - background services outside the job wrapper.
- No `float`, `double` or `decimal` field or property anywhere (invariant 1).

**Modules plug in without shared edits.**
- Each module's `IZawedModule` is discovered from the assembly.
- Controllers and EF configurations are discovered too.
- So parallel agents never edit `Program.cs`, `PlatformSetup.cs` or the `DbContext`.

### 2. Data access: EF Core where it fits, explicit SQL on the money path

| Work | Tool | Rule |
|---|---|---|
| Master data CRUD and reads (companies, departments, vehicles, stations, operators, prices, tickets, leads) | **EF Core 10 (Npgsql)**, `ZawedDbContext` | `AsNoTracking` by default, `Select` into DTOs, pagination in SQL, `ExecuteUpdateAsync` for bulk changes (playbook data-access §3). Entities map onto the existing tables: EF never creates or migrates anything. |
| Ledger postings, wallet and payable locks, the fueling engine, offline ingest, reconciliation, idempotency keys, job records, purges, rate-limit counters, reports | **Dapper on `DbTx`** (explicit SQL) | `SELECT ... FOR UPDATE` in the documented lock order. Row-count checks on `UPDATE ... WHERE status = 'PENDING'`. Calls to the SECURITY DEFINER functions. The statements of the Java stores are ported as they are. |

- Both run on the same connection and transaction (`DbTx.Db()` binds the context to it), so one request commits or rolls back as one.
- Enums are declared with their wire and database names (`SUPER_ADMIN`, `PENDING_APPROVAL`). One spelling then serves the JSON, the CHECK constraints and EF's `HaveConversion<string>()`; a naming policy cannot drift from Java's `TOPUP_APPROVE`. The OpenAPI parity test compares every enum.
- Dapper sends a C# enum as a number, so a store passes `value.ToString()`.
- Times are `DateTimeOffset` or UTC `DateTime` (`timestamptz`); JSON writes them with `Z`. Money and volume are `long` (ADR 0006).

### 3. Tenancy: row-level security stays the authority, EF filters are the second layer

- **The runtime role.** The application connects with the `PG*` credentials. Every physical connection of the runtime pool runs `SET ROLE zawed_app` (the Java `connection-init-sql`). `No Reset On Close` keeps the role: Npgsql's `DISCARD ALL` would otherwise return a pooled connection to the owner.
- **The scope, at transaction start.** Every `DbTx` begins READ COMMITTED and, in one round trip, runs `set_config('app.scope_type'|'app.scope_id'|'app.user_id', ..., true)` and reads `current_user`. It refuses to continue unless the role is `zawed_app` (the Java `ScopedTransactionManager`). The settings are transaction-local, so a pooled connection never carries a scope over.
- **One transaction per request** (`TransactionFilter`, the playbook's `TransactionActionFilter`):
  - it covers every action, reads included, because RLS needs the scope to show any row;
  - it begins after authorisation and model binding;
  - it rolls back on an exception or any status of 400 or more, and commits before the response is written;
  - `DbTx.AfterCommit` runs work after the commit only (the playbook outbox);
  - outside a transaction, `IDbSession.Current` throws, so no query runs unscoped by accident.
- **Own transactions** (`ITransactions.RunAsync`, the Java `REQUIRES_NEW` and `TransactionTemplate` cases), for actions marked `[OwnTransactions]`:
  - writes that must survive a refusal: lockout counters and failure audits at sign-in, rate-limit counts;
  - one transaction per offline-sync item, per import row, per purge batch;
  - a fueling retried once as a deadlock victim (`retryOnConflict`);
  - streaming exports;
  - jobs.
- **Escalation.** A scope that does not come from the token is built only with `DbScopeContext.Escalate`. That covers sign-in, the POS channel, the engine, sync, the fleet import worker and jobs, each after its own proof. `ArchitectureTests` keeps the callers to a reviewed list, as the Java `ESCALATIONS` did for `DbScope.runAs`.
- **EF global query filters**, the playbook's multi-tenancy model:
  - entities implementing `ICompanyOwned` or `IOperatorOwned` are filtered to the transaction's scope;
  - `SaveChangesAsync` refuses a write outside it;
  - such a refusal answers **404**, never 403: a row outside the scope looks like a row that does not exist (invariant 8);
  - RLS still decides. The filters only make a mistake show up in a test before RLS has to catch it.

### 4. Concurrency: the playbook's layers, in PostgreSQL terms

| Playbook (SQL Server) | ZAWED (PostgreSQL) | Where |
|---|---|---|
| `sp_getapplock` | `pg_advisory_xact_lock(hashtextextended(key, 0))` (`DbTx.AdvisoryLockAsync`), and `pg_try_advisory_xact_lock` for a job that should skip | Reconciliation (inside `ledger_reconcile`), any decision over a set of rows |
| Row lock | `SELECT ... FOR UPDATE` in the documented order: wallet, then vehicle (shared), then tag; wallet, then payable; top-up row, then wallet | Ledger, engine, station finance, maker-checker |
| Re-check after the lock | Unchanged: `clientTxnId` looked up before and after the wallet lock; the balance check is the decision's last step under the lock | Engine (ADR 0018) |
| Filtered unique index | Unchanged in the schema (partial unique indexes, `(source_type, source_id)`, `client_txn_id`); a unique violation from a concurrent duplicate is answered by re-reading | Engine, ledger, payouts, tags |
| `rowversion` | `xmin` as the EF concurrency token (`IVersioned`). A stale update raises `DbUpdateConcurrencyException`, answered `409 CONFLICT`. Never for money: balances change only under `FOR UPDATE` (invariant 5). | Master-data edits two people can make at once |
| Deadlock 1205 | `40P01` and `40001`: retried once where the Java code retried; otherwise `409 CONFLICT`, retryable | `ITransactions.RunAsync(retryOnConflict)` |

### 5. Idempotency keys keep ADR 0022's semantics, not the playbook's response replay

`IdempotencyKeys.ClaimAsync` stores, per user, operation and key:
- the SHA-256 of the body;
- the resource the key created;
- all in the transaction that creates the resource.

So:
- a replay returns the same resource;
- another body is `409 IDEMPOTENCY_KEY_REUSED`;
- a rollback frees the key;
- a concurrent duplicate waits on the primary key.

Where a duplicate would move money, `ClaimRequiredAsync` answers `400 INVALID_IDEMPOTENCY_KEY` on field `Idempotency-Key`. It is called from the service, after the permission check.

The playbook's filter replays a stored response and answers 422 or 409. That is not what the web client and ADR 0022 expect, so it is not used. Its good parts are kept: the claim runs inside the request transaction and is tested with concurrent requests. The purge stays `purge_idempotency_keys` (V17).

### 6. Results and the error contract

**Services and controllers.**
- Services return `Result` / `Result<T>` (playbook principle 2).
- A refusal carries exactly what the envelope needs: status, stable `code`, English message, `field`, `fields`, `params`.
- `ApiControllerBase.From(result)` writes success (200, 201 or 204) or the envelope.
- Below a service (stores, the money path, filters), `ApiException` and `FieldsInvalidException` remain allowed, as in Java. Both reach the client identically.

**`ApiError`, byte for byte the Java envelope:**
- `statusCode`, `code`, `message`, `retryable`;
- `requestId`, `field`, `fields[{field, code, message}]`, `params`, `retryAfterSeconds`;
- `null` members are left out.

One writer (`ApiErrors`) serves every refusal path:
- the exception handler;
- model validation;
- JWT challenge and forbid;
- the permission filter;
- the canonical path check;
- rate limits;
- the status-code pages for bare 404, 405 and 415.

**What each failure answers:**
- An unexpected exception answers `500 INTERNAL_ERROR` with only the request id; nothing internal is echoed.
- A fraction for a whole-number field answers `400 VALIDATION_FAILED`, `INTEGER_REQUIRED` on that field (Jackson's `accept-float-as-int: false`).
- Any other unreadable body (an unknown property, a wrong type, broken JSON, no body) is a plain `400 BAD_REQUEST`.
- A broken rule names its field and its Jakarta code. `Zawed.Api.Platform.Web.Validation` provides `NotBlank`, `NotNull`, `Size`, `Min`, `Max`, `Positive`, `Pattern` and `Email`.
- In a positional record, the attributes go on the parameter, not on `property:`.

**Deliberate change (owner question 1).** Three database outcomes that reached the Java handler as a 500 now answer 409, following the playbook:
- a unique violation nobody handled;
- a deadlock or serialization failure that escaped its retry;
- a stale `xmin`.

### 7. Authentication, sessions and devices

**User access tokens (ADR 0008):**
- JWT, HS256 only, issuer `zawed-api`, claims `typ=user`, `sid`, `sub`, 30 s skew, 15 minutes;
- the session row is read on every request (`ISessionVerifier`, implemented by identity), so role and scope come from the database;
- a token that fails is refused with 401 even on a public route, as Spring's bearer filter does.

**Deny by default:**
- the fallback authorisation policy requires a signed-in user;
- an unknown route answers 401 to an anonymous caller, 404 to a signed-in one, 405 for a known path with another method.

**Permissions:**
- `Role` and `Permission` are ported unchanged, with the role table;
- `[RequiresPermission(Permission.X, Scopes = [...])]` is an MVC **authorisation filter**, so it runs before model binding, as Java's `PermissionFirst` did;
- `ArchitectureTests` requires every action to be `[AllowAnonymous]`, `[RequiresPermission]` or on the reviewed signed-in-only list. The authorisation matrix and the IDOR sweep are ported as tests.

**Passwords and PINs:**
- Argon2id with the same parameters: m=19456 KiB, t=2, p=1, 16-byte salt, 32-byte hash;
- Spring's encoded form (Konscious.Security.Cryptography);
- a hash written by the Java code verifies here (`CryptoTests`, golden value from `Fixtures/Golden.java`);
- `Burn` keeps unknown-email timing equal.

**Refresh, MFA, lockout, sessions** (ported with identity, plan wave 1):
- the cookie `zawed_rt`, `Path=/api/v1/auth`, HttpOnly, SameSite=Strict, Secure when deployed;
- rotation, reuse detection and the 60-second grace window;
- the CSRF rule on refresh and logout: `Origin` in the allowed list and `X-Zawed-Client: web`;
- TOTP as in `Totp.java`;
- lockout: 5 failures for 15 minutes;
- the platform idle limit;
- the `PASSWORD_CHANGE_REQUIRED` gate (built in phase 0);
- the narrow SECURITY DEFINER doors of V3 for the sign-in path.

**The POS channel (ADR 0010, 0018):**
- `/api/v1/pos/**` gets its own authentication scheme: device tokens signed with a key derived under their own label, `typ=device`, plus the agent session header;
- a user token never passes there and a device token never passes elsewhere;
- the refusals that tell a terminal what to do next stay the same (`AGENT_SESSION_REQUIRED`, `PIN_CHANGE_REQUIRED`);
- nonces and the timestamp window are kept.

### 8. Crypto: the same formats, so every stored value stays readable

- **`DataEncryptor`:** AES-256-GCM, `[key id][nonce 12][ciphertext][tag 16]`, with associated data = the key id byte followed by the row binding, and the key ring of ADR 0027. A value written by the Java code under key 1, and one under key 200 (unsigned id), decrypt here.
- **`LookupHasher`:** `HMAC(HMAC(master, "zawed-lookup:" + ns), value)` in lowercase hex. It gives the same tag hash and the same `data_key_state` fingerprint as Java.
- **`DataKeyState`:** runs after the migrations on the owner's connection, with the same mismatch refusal.
- **`DataKeyRotation`:** the job is ported with each module's encrypted-column bindings (plan wave 1).

### 9. Background work and health

**Jobs:**
- A job is a `ScheduledJob`: a `BackgroundService` with a `PeriodicTimer` and an initial delay.
- Each run goes through `Jobs.RunAsync`, which records it in `job_runs` through `job_run_record`, counts only, and the error's type name only (ADR 0028).
- Locks stay where they were: `pg_try_advisory_xact_lock` inside `ledger_reconcile`, none for the purges, `FOR UPDATE SKIP LOCKED` plus a lease for the fleet import worker.
- No Hangfire: it would add tables, a dashboard and a second scheduler, and buy nothing over a timer, an advisory lock and `job_runs`. The playbook's own `BackgroundService` pattern is enough (ADR 0011: no extra infrastructure).

**After-commit work:**
- Synchronous domain events (`IDomainEvents`) run inside the publisher's transaction, as Spring's `@EventListener` with MANDATORY propagation did. Examples: `CompanyCreated` opens the wallet; `WalletLowBalance` raises an alert; notifications are written in a savepoint (`DbTx.SavepointAsync`).
- Work that must wait for the commit uses `DbTx.AfterCommit`.
- The Spring Modulith outbox had no consumer that needed durability, so nothing replaces it.

**Health (the Java JSON and status codes exactly):**
- `/api/v1/health/liveness` answers `{"status":"UP"}`.
- `/api/v1/health/readiness` checks that the instance is accepting traffic and the database answers. Railway's deploy probe reads it.
- `/api/v1/health` adds the jobs: DOWN (503) when a job is OVERDUE, WARN (200) when one is FAILING, cached for 30 s.
- The media type is `application/vnd.spring-boot.actuator.v3+json`.
- `/api/v1/meta` keeps the same shape. Its `version` ends in `-dotnet`, so anyone can tell the two APIs apart.

### 10. Rate limiting

- Both limiters of ADR 0012 are ported as ZAWED services, not ASP.NET Core's rate-limiting middleware:
  - the in-memory fixed window keyed by a hashed subject: sign-in 10/min per address, refresh 30/min per session and 300/min per address, 300/min per user;
  - the PostgreSQL limiter (`rate_limit_hit`, in its own transaction) for the demo form.
- Why not the middleware: several limits run inside services (per e-mail, the silent 202 of the demo form), and paths are matched on the canonical path. The middleware can express neither.
- The answer is the same: `429 RATE_LIMITED` with `Retry-After` and `retryAfterSeconds`.

### 11. The web edge

- **Request id.** `X-Request-Id` is kept when it is plain, otherwise replaced by a UUID. It is put in a log scope and re-set on error responses.
- **Canonical path.** `400 PATH_NOT_CANONICAL` on the raw request target, before CORS, authentication and routing (ADR 0012 amendment). Tested on a real Kestrel socket, because `HttpClient` unescapes `%74`.
- **Security headers.** Spring Security's defaults (checked against zawed-dev): no-cache unless the endpoint set its own, `nosniff`, `DENY`, `X-XSS-Protection: 0`, HSTS on HTTPS.
- **CORS** is answered as Spring answers it:
  - an allowed preflight is 200;
  - a cross-origin request from an unlisted origin is 403 before anything runs;
  - `Vary` is set on everything;
  - allowed headers and exposed headers are as in `SecurityConfig`.
- **Forwarded headers** are honoured only in the deployed profile, and only from Tomcat's default internal ranges, which include Railway's `100.64.0.0/10`.
- **OpenAPI** at `/api/v1/openapi.json` (Microsoft.AspNetCore.OpenApi, 3.1), shaped like springdoc's:
  - info, server `/`, the three security schemes;
  - per-operation security from the same path rules;
  - `ApiError` as every operation's `default` response;
  - integer formats, `required` = non-nullable.
- **Swagger UI** at `/api/v1/docs/` (switch: `ZAWED_API_DOCS`).
- **`OpenApiParityTests`** compares the two documents operation by operation.

### 12. Migrations: the SQL stays the schema source, and the history continues

- The 25 SQL files are copied byte for byte into `apps/api-dotnet/db/migration` and embedded. A test fails if they differ from `apps/api`'s while that folder exists, so a new migration (`V24+`) is added to both in the same commit until the cutover.
- **Why a copy.** Railway builds `zawed-api` from its root directory, so the image can only see `apps/api-dotnet`.
- **`MigrationRunner` is Flyway-compatible on Flyway's own table**, `flyway_schema_history`:
  - Flyway 12's exact DDL;
  - the same `version`, `description`, `type`, `script` and `installed_by = current_user`;
  - **Flyway's checksum**: CRC32 over the lines without line breaks, a leading BOM dropped, stored signed;
  - the checksum is reproduced, not baselined, and verified against the rows real Flyway 12.4 wrote for all 25 files.
- **Validation follows Flyway's defaults, before anything is applied.** The start stops on:
  - a failed row;
  - a changed checksum or description;
  - an unknown applied version, unless it is newer than every local file ("future", ignored as Flyway does);
  - a pending version older than the latest applied (out of order stays off).
- **Applying.** Each migration runs in its own transaction with its history row, under an advisory lock, so two instances migrate one after the other. The migrations and `DataKeyState` run as the database owner. The application runs as `zawed_app`, as today (V3, V5).
- **Tests:**
  - a fresh database gets Flyway's history rows and the **same `pg_dump --schema-only`** as real Flyway;
  - a database real Flyway migrated to V16 continues to V23, Flyway's rows untouched (fixtures in `tests/Zawed.Tests/Fixtures`, made with `RunFlyway.java`);
  - concurrent starts, a changed file, a failed row and a failing migration are covered.
- **Rollback is possible.** The history the .NET runner writes is valid Flyway history, so the Java API still starts on a database the .NET API migrated.

### 13. Deployment and configuration

- **Image.** `apps/api-dotnet/Dockerfile`: the SDK image builds, the `aspnet:10.0` image runs as the non-root `$APP_UID` on `PORT`. Railway's service changes only its root directory and watch pattern.
- **The same variables.** Railway's variables need no change:
  - `ZAWED_ENVIRONMENT`, `ZAWED_CORS_ALLOWED_ORIGINS`, `ZAWED_JWT_SECRET`;
  - `ZAWED_DATA_KEY`, `ZAWED_DATA_KEY_ID`, `ZAWED_DATA_KEYS_PREVIOUS`, `ZAWED_LOOKUP_KEY`;
  - `ZAWED_BOOTSTRAP_TOKEN`, `ZAWED_MFA_REQUIRED`, `ZAWED_API_DOCS`;
  - `PGHOST`, `PGPORT`, `PGDATABASE`, `PGUSER`, `PGPASSWORD`;
  - `PORT`, `SENTRY_DSN`.
- **`SPRING_PROFILES_ACTIVE=deployed`** is still how a deployment says it is one (`ZAWED_PROFILE=deployed` is accepted too). It means:
  - no development defaults are loaded;
  - cookies are Secure;
  - forwarded headers are honoured;
  - logs are one JSON object per line.
- **`DeploymentGuard` and `ZawedOptionsValidator`** carry every boot check of the Java `DeploymentGuard` and `ZawedProperties`, with the same messages:
  - the profile and the environment must agree;
  - production refuses `ZAWED_MFA_REQUIRED=false`;
  - origins: https only, never localhost, no wildcard;
  - secret lengths;
  - the committed development secrets anywhere, including as a previous key or the lookup key;
  - every key-ring rule of ADR 0027.
- **Time and ids.** `TimeProvider` is the only clock (ADR 0007), `FakeTimeProvider` in tests. Ids are `Guid.CreateVersion7` (ADR 0005, the same RFC 9562 layout).

### 14. Tests

- **The engine.** xUnit runs against a real PostgreSQL 18 started by **Testcontainers** (`postgres:18`): one container per run, removed by the reaper. Docker replaces the embedded binaries of ADR 0013.
- **The application.** `WebApplicationFactory<Program>` runs the real pipeline, migrations included. A real Kestrel socket is used where raw requests matter.
- **Support** (Java `support`): `Owner` for arranging and inspecting as the owner, `Seed` for companies, operators and users, `ProbeApi` with test-only endpoints, and a token minter.
- **Ports.** Each module's Java tests are ported by class name (plan). Concurrency and idempotency are tested with parallel requests on the real database, never with mocks.

## Consequences

- **The contract is checked mechanically.** The OpenAPI parity test, the migration tests and the golden crypto values make "same contract, same data" a test result, not a promise.
- **Phase 0 ships:**
  - the platform (§3 to §13);
  - the migration runner;
  - the test harness;
  - meta and health;
  - the parity test with 176 operations pending.
- **Next.** Modules are ported in the waves of `dotnet-migration-plan.md`. Until the last wave, the Java API stays the one serving zawed-dev.
- **Two copies of the migrations** live until the cutover; a test keeps them identical.
- **Accepted cost of the playbook's request transaction.** A write that must outlive a refusal has to be written in its own transaction, and each module plan lists those writes.
- **Rollback stays possible.** The history is Flyway's, so before any `V24`, switching Railway's root directory back to `apps/api` restores the Java API on the same database.

## Owner questions

1. **409 instead of 500.** An unhandled unique violation, an escaped deadlock and a stale row version now answer `409 CONFLICT` instead of `500` (§6). Accept, or keep 500 for parity?
2. **The live e2e suite.** `apps/web/playwright.live.config.ts` answers from a stub (`e2e/live/api-stub.ts`), so it cannot test a real API. The plan proposes a new `playwright.real.config.ts` in `apps/web`, which runs the same specs against a seeded API, Java first and then .NET. It is a web change; approve it.
3. **After the cutover.** When `apps/api` is removed, should the web's `api:types` script read `apps/api-dotnet`'s committed OpenAPI document (generated by a test)?
4. **CI.** The .NET suite needs Docker in CI (GitHub Actions has it). Add a `dotnet` job next to the Maven one now?
