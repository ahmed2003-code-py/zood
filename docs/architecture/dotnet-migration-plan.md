# Moving the API to ASP.NET Core: the port plan

**Status:** Proposed, 2026-11-21. Phase 0 is done (below). **Decisions:** [ADR 0029](../adr/0029-backend-on-aspnet-core.md). **Branch:** all .NET work lives on `dotnet-migration` (owner, 2026-11-21); agents work on short-lived local branches merged into it, never pushed on their own.

**The goal:** `apps/api-dotnet` serves the same HTTP contract as `apps/api`, on the same PostgreSQL schema and data. The web app and the Kotlin POS must not notice the switch. zawed-dev switches only after full parity **and only when the owner says so**, once he has tested the .NET API himself.

## 1. What phase 0 delivered

| Area | Where | Proven by |
|---|---|---|
| Solution, central package versions, Dockerfile | `apps/api-dotnet/` | `dotnet build` |
| Configuration: same variables, `SPRING_PROFILES_ACTIVE=deployed`, every Java boot check | `Platform/Config` | `ZawedOptionsTests` (ports `ZawedPropertiesTests` and `DeployedStartupTests`) |
| `ApiError`, `Result<T>`, `ApiControllerBase.From`, the exception handler, the validation codes, JSON whole numbers | `Platform/Web` | `RequestPipelineTests`, `ApiSmokeTests` |
| Request id, canonical path, security headers, CORS, deny by default | `Platform/Web`, `Platform/Security` | `ApiSmokeTests`, `CanonicalPathTests` (real Kestrel) |
| The scoped transaction (`DbTx`, `TransactionFilter`, `ITransactions`, `DbScopeContext.Escalate`), the runtime-role guard, the EF context with filters and the write guard | `Platform/Persistence` | `RequestPipelineTests` |
| Access tokens, `ISessionVerifier`, `[RequiresPermission]` before binding, `Role` and `Permission`, the password-change gate | `Platform/Security` | `RequestPipelineTests` |
| `IdempotencyKeys` (ADR 0022), `IDomainEvents`, `Page<T>`, `ClientInfo` | `Platform/Persistence`, `Platform/Events`, `Platform/Web` | `RequestPipelineTests` (8 concurrent requests on one key) |
| `DataEncryptor`, `LookupHasher`, `PasswordHasher` (Argon2id), `SecureTokens`, `DataKeyState` | `Platform/Crypto` | `CryptoTests` (values written by the Java code) |
| `Jobs`, `ScheduledJob`, `JobsHealth`, the health probes, `/api/v1/meta` | `Platform/Jobs`, `Platform/Health`, `Platform/Meta` | `ApiSmokeTests` |
| The migration runner, continuing `flyway_schema_history` | `Platform/Persistence/Migrations` | `MigrationRunnerTests` (real Flyway fixtures) |
| OpenAPI document and Swagger UI | `Platform/OpenApi` | `OpenApiParityTests` |
| Module boundaries and the reviewed lists | — | `ArchitectureTests` |

**Not in phase 0, assigned below:**
- the rate limiters;
- the Ops jobs endpoints;
- the retention and rotation jobs;
- `Money`, `Volume` and the fueling rules;
- every business module.

## Progress

| Wave | State | Notes |
|---|---|---|
| 1A Identity and audit | Done on `local/dn-1a` (2026-11-21), awaiting `zawed-reviewer` | 18 operations off the parity list. Ported: `SignInTests`, `SignInWithoutMfaTests`, `SessionTests`, `TotpTests`, `BootstrapTests` (on a database of its own), `UserAdminTests`, `SessionRetentionTests`, `PathMatchingTests`, `NonSuperuserOwnerTests` (migrated by the .NET runner), `RowLevelSecurityTests` (users and audit), the `AuthorizationMatrixTests` harness with the whole Java table (a row is checked once its endpoint is served), the limit half of `SecurityFiltersPathMatchingTests` (`SecurityLimitsTests`), `AuditLogTests`. Not yet: the matrix's multipart upload test (2C), the POS half of `PathMatchingTests` (2B). Platform changes: `Platform/RateLimit` (in-memory limiter, the identity limits as middleware), `StrictRouting` (case and trailing slash as Spring), `RateLimitedException` with `params`, a path or query value that does not convert answers `400 BAD_REQUEST` (Spring's type mismatch; 1C's `Validation.IsEncoded` rule kept at the merge), OpenAPI names, inline enums and required `[NotNull]` properties as springdoc writes them. Escalations: `SignIn`, `Sessions`. `IdentityTestSupport` gives later waves a signed-in client for any role. |
| 2A Ledger | Done on `local/dn-2a` (2026-11-21), merged with 2B and 2C, awaiting `zawed-reviewer` | 22 operations off the parity list. 62 tests in `Modules/Ledger`: the six Java classes plus the extras listed under 2A. Contracts: `ILedger`, `IWalletBalances`, `BankReferences`, the five ledger events; the `CompanyCreated` handler opens the wallet. Job: `ledger-reconciliation` (02:30 Cairo). No escalation. |
| 2B Network and the POS channel | Done on `local/dn-2b` (2026-11-21), awaiting `zawed-reviewer` | 32 operations off the parity list. Ported: `NetworkTests`, `PosChannelTests` (the channel half; where Java probed the agent gate with a fueling, the same request now answers from the POS chain and then 404 until 3A), `NonceRetentionTests`, the operator lines of `CompanyContactTests` (`OperatorContactTests`), the POS halves of `PathMatchingTests` and `SecurityFiltersPathMatchingTests` (`Platform/PosChainTests`). Signatures in the tests are WebCrypto's r‖s turned into DER, as the web POS does. Platform piece: `Platform/Security/PosChannel` (the `Pos` scheme, the chain middleware right after `UseAuthentication`, Java's path rules and refusals); the user JWT scheme no longer reads a bearer on `/api/v1/pos/**` (1A review) and the fallback policy names both schemes. Data access is Dapper with the Java statements (device security path; `fuel_types` arrays; coordinates as exact text in `Coordinate`, no `decimal`). Contracts: `IOperatorDirectory` (pricing's stand-in replaced; user admin checks an operator scope through it), `IStationDirectory`, `IAgentShifts`, `PosPrincipal`. Escalations: `DeviceChannel`, `AgentSessions`. Job: `nonce-retention`. Fixed in passing: `PatternAttribute`'s message broke on a `{8}` quantifier. 54 tests in `Modules/Network` plus 36 POS chain cases. |
| 2C Fleet and leads | Done on `local/dn-2c` (2026-11-21), merged with 1A and 1B, awaiting `zawed-reviewer` | 23 operations off the parity list (fleet 20, leads 3). 128 tests in `Modules/Fleet` and `Modules/Leads`, details under 2C. Escalation: `FleetImportWorker`. |
| 3B Tickets | Done on `local/dn-3b` (2026-11-21), awaiting review | 12 operations off the parity list. 15 tests in `Modules/Tickets`, details under 3B. No escalation, no platform change. |
| 3A Fueling (online) | Done on `local/dn-3a` (2026-11-21), awaiting `zawed-reviewer` | 18 operations off the parity list. 40 tests in `Modules/Fueling` (details under 3A). Contracts: `HardAlertRaised`, `IFuelingApi`. Escalations: `FuelingEngine`, `Receipts`. |
| 4B Station finance | Done on `local/dn-4b` (2026-11-21), awaiting `zawed-reviewer` | 27 operations off the parity list. 37 tests in `Modules/StationFinance` (details under 4B). Contracts: `PayoutPosted`, `BankAccountChangeDecided`. Encrypted column: `station_bank_accounts.account_encrypted`. No escalation, no platform change. |

**For 1B (rate limiter), what 1A put in `Platform/RateLimit`:**
- `InMemoryRateLimiter.cs`: `public interface IRateLimiter { void Acquire(string key, int limit, TimeSpan window); }` (Java `RateLimiter`) and `public sealed class InMemoryRateLimiter(TimeProvider) : IRateLimiter`, fixed windows in a `ConcurrentDictionary`, ended windows swept lazily at most once a minute (no timer), `Reset()` for tests. Throws `RateLimitedException(retryAfterSeconds)`, which now carries `params.retryAfterSeconds` (`Platform/Web/ApiException.cs`).
- `SecurityLimits.cs`: `UseZawedRateLimits()` middleware and the testable `Apply(HttpContext, IRateLimiter)`: sign-in steps 10/min per hashed address (any method), refresh 30/min per cookie hash plus 300/min per address, 300/min per signed-in user; constants `SignInPerMinute`, `RefreshPerMinute`, `RefreshPerAddressPerMinute`, `AuthenticatedPerMinute`, `RefreshCookie`. It writes the 429 envelope itself (with `Retry-After`).
- `PlatformSetup.cs`: `services.AddSingleton<InMemoryRateLimiter>()` with `IRateLimiter` forwarding to it, and `app.UseZawedRateLimits()` right after `UsePasswordChangeGate()` (Java's filter order). `app.UseStrictRouting()` right after `UseRouting()` (`Platform/Web/StrictRouting.cs`).
- Users: `SignIn` (`signin-email:` key, 10 per 15 min), `Account` (`password-change:` key, 10 per 15 min); tests reset it through `IdentityTestSupport`. Tests: `Platform/SecurityLimitsTests.cs`. `PostgresRateLimiter` does not implement `IRateLimiter` (as in Java) and can sit beside it in the folder.

## 2. Rules every module agent follows

**Read first:**
- `apps/api-dotnet/CLAUDE.md` (where things go, the conventions);
- ADR 0029;
- the Java package you port and its tests;
- the module's contract in `docs/architecture/*-contract.md`.

**The port is literal:**
- the same endpoints, status codes, error codes, `field`s and `params`;
- the same SQL statements on the money path, the same lock order, the same audit rows;
- the same order of validation checks;
- the Java code and its tests are the specification.
- An improvement is a separate, named change after parity, never part of the port.

**Where your code goes:**
- `src/Zawed.Api/Modules/<Name>/` for the module;
- `Modules/<Name>/Contracts/` for anything another module may use (interfaces, events, records);
- `tests/Zawed.Tests/Modules/<Name>/` for the tests.
- Do not edit another module's folder, `Program.cs` or `PlatformSetup.cs`. A platform gap goes to the wave's platform owner, or is reported.

**OpenAPI.**
- Delete your operations from `tests/Zawed.Tests/OpenApi/pending-operations.txt`.
- `OpenApiParityTests` then compares them with the Java document: operation ids, tags, parameters, bodies, responses and every schema they reach, by name.
- Name DTO records exactly as the Java records are named (`CompanyResponse`, `PageCompanyResponse` ...). Use `[EndpointName("<operationId>")]` (or `Name =` on the HTTP attribute) and `[Tags("<tag>")]`.

**Tests:**
- Port every Java test class listed for your module, test for test, against the real PostgreSQL (`SharedApi`, or `ProbeApi` before identity exists).
- Money and concurrency tests use parallel requests, never mocks.
- A Java test that does not apply to .NET is listed in your report with the reason.

**Escalations and lists.** A new `DbScopeContext.Escalate` caller, signed-in-only endpoint, raw-path reader or clock reader goes into `ArchitectureTests` with its reason, and is pointed out in your report.

**Money and security modules** (identity, network, ledger, fueling, sync, station finance) are done by `zawed-deep` and closed by a `zawed-reviewer` pass.

**Definition of done (every module):**
1. `dotnet build` and `dotnet test` are green, and nothing is skipped.
2. The module's operations are gone from `pending-operations.txt`, and parity is green for them.
3. Every listed Java test class is ported or explained.
4. `ArchitectureTests` is green, with any list change explained.
5. The report lists the files, the tests per invariant, the commands run and their output, and the open questions.
6. Commits are on the agent's local branch, and `git status` is clean.

## 3. The waves

At most three agents at once. A wave starts when the one before it has merged into `dotnet-migration`. Within a wave the three agents touch disjoint folders, so they need no coordination beyond the contracts named here.

### Wave 1: identity, the platform's remainder, and tenancy

**1A. Identity and audit** (`zawed-deep`)

**Java to read:**
- `identity/*`: `SignIn`, `Sessions`, `AuthController`, `AuthCookies`, `AccessTokens`, `IdentityStore`, `Totp`, `PasswordPolicy`, `UserAdmin`, `AdminUsersController`, `SetupController`, `SessionRetention`, `ActorDirectory`, `Actors`, `IdentityApi`;
- `audit/*`;
- `platform/security/CsrfRule`, `SecurityFilters` (the sign-in limits);
- contract `b1-identity-contract.md`, ADR 0008 (with its amendment), ADR 0009.

**Endpoints (18):**
- `POST /auth/login`, `/auth/mfa/verify`, `/auth/mfa/enroll/start`, `/auth/mfa/enroll/confirm`, `/auth/refresh`, `/auth/logout`, `/auth/password`;
- `GET /auth/me`, `GET /auth/sessions`, `DELETE /auth/sessions/{id}`;
- `GET|POST /admin/users`, `POST /admin/users/{id}/status|reset-password|unlock|mfa/reset`;
- `POST /setup/super-admin`;
- `GET /admin/audit`.

**Tables and functions:**
- tables `users`, `sessions`, `mfa_challenges`, `audit_log`;
- the V3 doors `auth_login_lookup`, `auth_record_login_failure`, `auth_session_by_id`, `auth_session_by_refresh_hash`, `auth_bootstrap_super_admin`, `auth_super_admin_exists`;
- `purge_sessions`, `purge_mfa_challenges`.

**Invariants:**
- constant-time sign-in (`Burn`);
- lockout after 5 failures for 15 minutes, with the counters and failure audits **in their own transaction** (they must outlive the 401);
- refresh rotation, reuse detection, the 60 s grace window, role and status re-read;
- the cookie `zawed_rt` with `Path=/api/v1/auth`, HttpOnly, SameSite=Strict, Secure when deployed;
- the CSRF rule;
- MFA by role and `ZAWED_MFA_REQUIRED`;
- the 12 h platform idle limit;
- old `ADMIN`-door sessions are never refreshed;
- `ISessionVerifier` replaces the platform's `NoSessions`.

**Platform pieces this agent owns:**
- the in-memory rate limiter, with its 429 and `Retry-After`;
- the sign-in, refresh and per-user limits as middleware matching the canonical path;
- `SessionRetention` as a `ScheduledJob`.

**Tests to port:** `SignInTests`, `SignInWithoutMfaTests`, `SessionTests`, `TotpTests`, `BootstrapTests`, `UserAdminTests`, `SessionRetentionTests`, `PathMatchingTests`, `NonSuperuserOwnerTests`, `RowLevelSecurityTests` (users and audit). Also the **harness** of `AuthorizationMatrixTests`: a table of endpoint × role → expected status, which every later module extends with its rows.

**Also deliver:** `Support/IdentityTestSupport` (the Java helper of the same name): create a user, sign in through the API (MFA included), return a client with the bearer token. Every later wave uses it.

**1B. The platform's remainder and the pure domain** (main session, or `zawed-deep` for the limiter and rotation)

**Java to read:**
- `platform/ratelimit/*`, `platform/jobs/OpsJobsController`, `JobRunRetention`, `platform/persistence/IdempotencyKeyRetention`;
- `platform/crypto/DataKeyRotation`, `EncryptedColumn`;
- `platform/money/*`, `platform/text/EgyptianContact`;
- `rules/*` (`FuelingRules`, `Periods`), with the web's `apps/web/src/domain/fueling.ts`, `money.ts` and their tests.

**Endpoints (2):** `GET /ops/jobs`, `GET /ops/jobs/runs`.

**Tables and functions:** `rate_limit_counters` (`rate_limit_hit`, `purge_rate_limit_counters`), `job_runs` (`purge_job_runs`, `job_run_latest`), `idempotency_keys` (`purge_idempotency_keys`), `data_key_state` (`data_key_current_id`, `data_key_rekeyed`).

**Deliver:**
- `PostgresRateLimiter`, with its own transaction per hit;
- `RateLimitCounterRetention`, `JobRunRetention`, `IdempotencyKeyRetention`;
- the `DataKeyRotation` job over an `EncryptedColumn` registry that modules fill;
- `Money`, `Volume`, `MoneyMath`, `FuelingRules` and `Periods` in `Zawed.Domain`;
- `EgyptianContact`.

**Tests to port:** `PostgresRateLimiterTests`, `JobsTests`, `JobsHealthEndpointTests`, `IdempotencyKeyRetentionTests`, `MoneyAndVolumeTests`, `MoneyMathTests`, `FuelingRulesTests`, `EgyptianContactTests`, `ClientInfoTests`, `UuidV7Tests`, `SentryScrubberTests` (when Sentry is wired), `SecurityFiltersPathMatchingTests` (with 1A). `DataKeyRotationTests` moves to wave 5, once every encrypted column exists.

**Progress (2026-11-21, branch `local/dn-1b`):** done, awaiting merge (merged with 1C first).
- `GET /ops/jobs` and `/ops/jobs/runs` are served and out of `pending-operations.txt`; parity is green for them.
- Platform: `IRateLimiter`, `InMemoryRateLimiter`, `PostgresRateLimiter` (`Platform/RateLimit`); `IJobsHealth`, `JobRunStore`, `OpsJobsController`, `JobRunRetention`; `IdempotencyKeyRetention`; `RateLimitCounterRetention`; `DataKeyRotation` and `DataKeyRotationJob` over `EncryptedColumn`s that modules register with `services.AddSingleton(new EncryptedColumn(...))`; `Durations` (Spring and ISO-8601 settings, Java's `Duration` text). Domain: `Money`, `Volume`, `MoneyMath`, `Rules/FuelingRules`, `Rules/Periods`.
- 141 tests: the listed Java classes, plus the in-memory limiter, the Java spelling of the jobs' settings, and `DataKeyRotationJobTests` (the job's half of `DataKeyRotationTests`: skip-locked rows, key-share locks, resumable and parallel runs, key overlap recorded as a skip, unreadable values counted and never logged). The end-to-end half stays in wave 5.
- Not ported here: `SecurityFiltersPathMatchingTests` tests the sign-in and refresh limits, which 1A wires as middleware on top of `IRateLimiter`; `SentryScrubberTests` waits for Sentry.
- Reconciled with 1A at the merge: both waves wrote `IRateLimiter` and `InMemoryRateLimiter` with the same API; one copy is kept (1B's, whose window arithmetic has no floating point). `ScheduledJob.RunNowAsync` replaces `SessionRetention`'s own copy.
- Shared changes, reported for the merge: `PlatformSetup` registers the jobs and limiters (`AddJob<T>`); `ZawedConfiguration.Aliases` gains the jobs' variables; `JobsHealth` is behind `IJobsHealth` and carries `lastSuccessCounts`; `ScheduledJob.RunNowAsync`; `RateLimitedException` carries `retryAfterSeconds` in `params` as the Java body does; `ClientInfo` writes IPv6 keys in Java's uncompressed text (both APIs hash them into `rate_limit_counters`); `EgyptianContact` (1C's, kept) now blanks and matches as Java does; the `DataKeyRotation` escalation in `ArchitectureTests`.

**1C. Tenancy and pricing** (main session): the EF Core reference module.

**Java to read:** `tenancy/*`, `pricing/*`, contract `b2-master-data-contract.md`, ADR 0022 (company creation).

**Endpoints (13):**
- `GET|POST /ops/companies`, `GET|PUT /ops/companies/{id}`, `PUT /ops/companies/{id}/policy`, `POST /ops/companies/{id}/status`;
- `GET|POST /ops/companies/{id}/departments`;
- `GET /company/profile`, `GET /company/departments`;
- `GET|POST /ops/prices`, `GET /ops/prices/current`.

**Tables and functions:** `companies`, `departments`, `fuel_prices`, `tax_id_key`.

**Contract to publish:** `Modules/Tenancy/Contracts`: `CompanyCreated` (a domain event, handled in the onboarding transaction) and `ICompanyDirectory`. Ledger handles `CompanyCreated` in wave 2. Until then, a company commits without a wallet, which only the reconciliation would report.

**Tests to port:** `CompaniesTests`, `CompanyContactTests`, `CompanyIdempotencyTests`, `MasterDataIsolationTests`, `PricesTests`.

**Progress (2026-11-21, branch `local/dn-1c`):** done, awaiting merge.
- The 13 operations are served and out of `pending-operations.txt`; parity is green for them.
- 42 tests in `Modules/Tenancy` and `Modules/Pricing`: the five Java classes, plus the EF filter and write guard, `ICompanyDirectory` under a scope, the B2 rules the Java classes did not pin (pages, 404s, audit rows of every change, validation codes) and the Java body hash of an idempotent create.
- Moved to later waves: the operator lines of `CompanyContactTests` (network, 2B, which serves `/ops/operators`) and the wallet count of the onboarding race (ledger, 2A, whose `CompanyCreated` handler opens it).
- Stand-in: pricing checks a station with the Java `stationExists` statement until network publishes `IOperatorDirectory` (2B replaces it).
- Shared changes, reported for the merge: the audit contract copied from 1A byte for byte; `ZawedOpenApi` is 1A's version plus `format: email`; `Sql.Clean`, `EgyptianContact` (1B owns it: keep 1B's on a clash, the API is the Java one) and `FuelType` (in `Zawed.Domain`); the Jackson-compatible idempotency hash; a path or query value that does not convert answers `400 BAD_REQUEST`; the `ProbeApi` start race.
- How to write an EF Core module is in `apps/api-dotnet/CLAUDE.md`.

### Wave 2: ledger, network, fleet

**2A. Ledger** (`zawed-deep`)

**Java to read:** `ledger/*`, ADR 0017, ADR 0021, ADR 0022, contract `b3-ledger-contract.md`, plan §3.5.

**Endpoints (22):**
- company: `GET /company/wallet`, `/company/wallet/statement`, `GET|POST /company/topups`, `POST /company/topups/{id}/cancel`;
- Ops: `GET /ops/companies/{id}/wallet` and `/wallet/statement`, `POST /ops/companies/{id}/wallet/credits` and `/wallet/adjustments`;
- Ops top-ups: `GET /ops/topups`, `/ops/topups/{id}`, `/ops/topups/receiving-accounts`, `POST /ops/topups/{id}/verify|approve|reject`;
- Ops adjustments: `GET /ops/wallet-adjustments`, `POST /ops/wallet-adjustments/{id}/approve|reject|cancel`;
- reconciliation: `GET|POST /ops/reconciliation/runs`, `GET /ops/reconciliation/runs/{id}/findings`.

**Tables and functions:** `ledger_accounts`, `ledger_transactions`, `ledger_entries`, `company_wallets`, `station_payables`, `topup_requests`, `wallet_adjustments`, `reconciliation_runs`, `reconciliation_findings`, `ledger_reconcile`.

**Invariants (1, 4, 5):**
- integer piastres;
- every transaction balances and has a unique source;
- entries are never updated;
- a balance changes only under `FOR UPDATE`, in the entry's transaction;
- lock order: wallet then payable, and top-up row then wallet;
- maker-checker, with self-approval refused by the service and by the database;
- the duplicate source pre-checked after the wallet lock;
- `Idempotency-Key` required on adjustments;
- the bank reference unique per receiving account.

**Contract to publish:**
- `ILedger` with `LockWallet`, `LockPayable` and `Post`;
- the events `WalletLowBalance`, `TopUpAwaitingApproval`, `TopUpCredited`, `TopUpRejected`, `WalletAdjustmentAwaitingApproval`;
- the `CompanyCreated` handler that opens the wallet.

**Job:** `Reconciliation` (nightly), under the advisory lock inside `ledger_reconcile`.

**Tests to port:** `LedgerTests`, `LedgerIsolationTests`, `TopUpTests`, `WalletAdjustmentTests`, `MakerCheckerNamesTests`, `ReconciliationTests`. Concurrency: parallel approvals post once, and parallel postings on one wallet keep `balance_after` consistent.

**Progress (2026-11-21, branch `local/dn-2a`):** done, merged with `dotnet-migration` after 2B and 2C, awaiting `zawed-reviewer`.
- The 22 operations are served and out of `pending-operations.txt`; parity is green for them, including the `Idempotency-Key` header that `requestWalletAdjustment` documents as required while binding it as optional (a module operation transformer reads `[DocumentedRequiredHeader]`).
- Money path on Dapper with the Java statements: `LedgerStore` (`ILedger`: wallets locked in Java's signed `UUID.compareTo` order, then payables; the duplicate source checked after the locks; `ledger_transactions_reversed_once` told apart from the source key), `TopUps` (top-up row, then wallet), `WalletAdjustments` (`ClaimRequiredAsync`, hashed over `[companyId, body]` as Java hashed `List.of(companyId, body)`), `Wallets` (statement paged by `seq`), `Reconciliation` (`ledger_reconcile`, its advisory lock and the once-a-Cairo-night rule stay in the database).
- `ReconciliationJob` is a `ScheduledJob` whose loop waits for the next 02:30 in Africa/Cairo (Java's cron), skipping a night whose 02:30 does not exist; max age 27 h.
- 62 tests: the six Java classes test for test (with `Concurrently` on real transactions and HTTP), plus: 30 credits then 30 debits raced on one wallet with the `balance_after` chain checked; a posting refused half-way changes nothing and leaves its source free; a payable opened on first use and a reversal posted once; Java's UUID lock order; the low-balance, top-up and adjustment events recorded on commit and published once (raced approvals and replays included); the idempotency hash equal to the Java body's SHA-256; the reconciliation schedule across Egypt's offsets; the wallet half of the 1C onboarding race (one company, one wallet).
- Deviation in mechanism, not behaviour: the API runs with `InvariantGlobalization`, where `string.Normalize` leaves non-ASCII text as it is, so the bank reference's NFKC is PostgreSQL's `normalize(text, NFKC)` (the function V6_1 rebuilt the keys with); `BankReferences.KeyAsync`/`CleanAsync` take the transaction. The folding (Nd digits, Java's `toUpperCase`, whose only non-ASCII results in A-Z are ı and ſ, checked over every code point on JDK 25) is in C#; values checked against JDK 25.
- Settings read under the Java names: `Zawed:Finance:ReceivingAccounts` / `ZAWED_FINANCE_RECEIVINGACCOUNTS` (default `MAIN`) and `Zawed:Finance:ReceivingAccountLabels` / `ZAWED_FINANCE_RECEIVINGACCOUNTLABELS`; a malformed label stops the boot, as in Java. No shared file changed for them.
- No purge function belongs to the ledger (its tables are append-only or kept for ever).
- Shared changes: `FleetImportTests` funds its wallet through the top-up API (record, then a second person approves) instead of SQL.

**Review fixes (2026-11-21, branch `local/dn-2a-fix`):** the `zawed-reviewer` pass found no money-losing defect and four low findings that matter while Java and .NET instances serve side by side. All four are fixed, each with a test that fails on the old code.
- The `Idempotency-Key` body hash is now Jackson's byte for byte. `IdempotencyKeys.Json` writes the wire JSON with Jackson 3's string escaping: only `"`, `\` and U+0000 to U+001F are escaped (`\b \t \n \f \r`, otherwise upper-case `\u00XX`), and DEL, the no-break spaces, U+2028/2029, the BOM and non-BMP characters are written as they are. `UnsafeRelaxedJsonEscaping` escaped the last four groups. `IdempotencyHashTests` holds golden hashes from jackson-databind 3.1.7 on JDK 25 for NBSP, emoji, Arabic, the separators, control characters and `<>&'"\/`.
- Keys are read with Java's `isBlank()`/`strip()`, which do not count U+00A0, U+2007, U+202F or U+0085 as white space. Controllers pass the raw header (`IdempotencyKeys.KeyOf`), because MVC binds a value that .NET counts as white space to null. Tests: `RequestPipelineTests.AnIdempotencyKeyIsStrippedAsJavaStripsIt` and `WalletAdjustmentTests.AKeyAJavaInstanceStoredWithANoBreakSpaceReplaysHere`, where a key stored by Java with a leading NBSP replays instead of opening a second adjustment.
- `BankReferences.KeyAsync` refuses a reference that PostgreSQL cannot hold (U+0000, a lone surrogate) with `400 INVALID_BANK_REFERENCE` before `normalize()`, which would fail with 22021 and answer 500. This goes beyond Java, which accepts the NUL and then fails at its insert. Test: `TopUpTests.AReferenceWithANulCharacterIsRefusedBeforeTheDatabaseSeesIt` (request, record, verify).
- New shared kernel `Platform/Text/JavaText` (`IsWhitespace`, `Strip`, `IsBlank`, `Trim`, `ToUpperRoot`, `TryParseLong`), checked against JDK 25 over every code point (`JavaTextTests`). It replaces the copies in Ledger, Fleet and Network, and `Sql.IsJavaWhitespace` delegates to it. `ToUpperRoot` is `toUpperCase(Locale.ROOT)` (`ı`→`I`, `ß`→`SS`, `ﬁ`→`FI`), now used for receiving accounts (`TopUps`, `FinanceSettings`, whose label parsing also uses `isBlank`/`trim` now), device and activation codes (`DeviceChannel`), worker ids (`AgentSessions`) and the agents search. The statement cursor reads any Unicode decimal digit, as `Long.parseLong` does (`١٢٣` is 123). This is harmless on a cursor and kept for parity.
- Added: `LedgerTests.ADuplicateSourceThatSlipsPastTheCheckIsRefusedByTheUniqueIndex` (two wallets, so no shared lock; the second insert is seen waiting on the first transaction, then answered `ALREADY_POSTED` with nothing left behind) and `PostingsAcrossTwoWalletsInBothOrdersAtOnceNeverDeadlock` (40 postings, A→B and B→A).
- Not changed: NUL in other free-text fields (a note, a reason) still fails at the insert with 500, as in Java.

**2B. Network and the POS channel** (`zawed-deep`)

**Java to read:**
- `network/*`: `DeviceChannel`, `DeviceTokens`, `PosCredentials`, `PosPrincipal`, `AgentSessions`, `Agents`, `Devices`, `Operators`, `StationDirectory`, `Shifts`, `NonceRetention`;
- `platform/security/PosAuthenticator`;
- ADR 0010, ADR 0018 §1–2, ADR 0024;
- contract `b4-fueling-contract.md` §1.

**Endpoints (32):**
- operators: `GET|POST /ops/operators`, `GET|PUT /ops/operators/{id}`, `POST /ops/operators/{id}/status`;
- stations: `GET|POST /ops/stations`, `GET|PUT /ops/stations/{id}`, `POST /ops/stations/{id}/status`;
- devices: `GET|POST /ops/devices`, `GET /ops/devices/{id}`, `POST /ops/devices/{id}/activation-code|revoke`;
- agents: `GET|POST /ops/agents`, `GET /ops/agents/{id}`, `POST /ops/agents/{id}/status|reset-pin`;
- shifts: `GET /ops/shifts`;
- station portal: `GET /station/operator`, `/station/stations`, `/station/stations/{id}`, `/station/devices`, `/station/agents`, `/station/shifts`;
- POS: `POST /pos/activate`, `/pos/token`, `/pos/agents/login|logout|pin`.

**Tables and functions:** `station_operators`, `stations`, `pos_devices`, `agents`, `agent_sessions` (never purged), `device_nonces`, `purge_device_nonces`.

**Platform piece this agent owns:** the POS authentication scheme for `/api/v1/pos/**`:
- device tokens (`typ=device`, their own key label) and the agent session header;
- `POS_DEVICE`, `POS_AGENT` and `POS_AGENT_PIN_CHANGE`;
- the refusals `AGENT_SESSION_REQUIRED` and `PIN_CHANGE_REQUIRED`;
- user tokens refused on POS routes and device tokens refused elsewhere.

**Escalations to review:** `DeviceChannel`, `AgentSessions`.

**Tests to port:** `NetworkTests`, `NonceRetentionTests`, `PosChannelTests` (the channel half; the fueling half goes with 3A).

**2C. Fleet and leads** (main session; the import worker by `zawed-deep`)

**Java to read:** `fleet/*`, `leads/*`, ADR 0023, ADR 0026, contract `u1-leads-contract.md`.

**Endpoints (fleet 20):**
- vehicles: `GET|POST /ops/vehicles`, `GET|PUT /ops/vehicles/{id}`, `POST /ops/vehicles/{id}/status`, `POST /ops/vehicles/{id}/tags`;
- limits: `PUT /ops/vehicles/{id}/limits`, `DELETE /ops/vehicles/{id}/limits/{limitId}`;
- tags: `POST /ops/tags/{id}/status`;
- onboarding: `GET /ops/companies/{id}/onboarding`;
- imports: `GET /ops/fleet-imports`, `GET /ops/fleet-imports/template`, `GET /ops/fleet-imports/{id}`, `GET /ops/fleet-imports/{id}/rows`, `GET /ops/fleet-imports/{id}/errors.csv`, `POST /ops/companies/{id}/fleet-imports`, `POST /ops/fleet-imports/{id}/confirm|cancel`;
- company portal: `GET /company/vehicles`, `/company/vehicles/{id}`.

**Endpoints (leads 3):** `POST /public/demo-requests`, `GET /ops/leads`, `PATCH /ops/leads/{id}`.

**Tables and functions:**
- fleet: `vehicles`, `rfid_tags` (encrypted UID, `uid_lookup`), `limit_rules`, `fleet_import_jobs`, `fleet_import_rows`, `plate_key_canonical`;
- leads: `leads`, `submit_lead`, `purge_closed_leads`.

**Invariants:**
- the vehicle lock before tag changes;
- one active tag per vehicle;
- immutable identity columns;
- the import worker: `FOR UPDATE SKIP LOCKED` with a 2-minute lease, one transaction per row, counters recomputed;
- the XLSX/CSV limits: 5 MB, 5,000 rows, DOCTYPE/ENTITY refused, a zip bomb refused.

**Encrypted column:** register `rfid_tags.uid_encrypted` with the binding `Tags.associatedData`.

**Library choice:** the XLSX reader. Port `fastexcel-reader`'s behaviour with a streaming reader (for example `DocumentFormat.OpenXml` in SAX mode, or a small `System.IO.Compression` plus `XmlReader` with DTD processing prohibited), keeping every archive check. This is a library choice: name it in the report.

**Tests to port:** `FleetTests`, `FleetIdentityTests`, `FleetImportTests`, `DemoRequestTests`, `DemoRequestGateTests`, `DemoRequestTextTests`, `DemoRequestBehindProxyTests`, `LeadsIsolationTests`, `LeadsOpsTests`. Also `MigrationCompatibilityTests` (the V14 plate-key collision check) on the runner.

**Progress (2026-11-21, branch `local/dn-2c`):** done, merged with `dotnet-migration` after 1A and 1B, awaiting `zawed-reviewer`.
- The 23 operations are served and out of `pending-operations.txt`; parity is green for them (the upload documented as springdoc wrote it, by an operation transformer in `FleetModule`; `allowableValues` and the e-mail format by a schema transformer in `LeadsModule`).
- Fleet: `Vehicles`, `Tags`, `PlateKey` (NFKC, code point by code point, digits-first canonical key, tatweel-free search), the vehicle lock before every tag or limit change. `FleetImports`, `FleetImportFile`, `Xlsx`, `FleetImportWorker` (`FOR UPDATE SKIP LOCKED`, 2-minute lease, one transaction per row, counters recomputed at the end). Contracts for waves 3 and 4: `ITagGate` and `IFleetSnapshot` (Java `TagGate`, `FleetSnapshot`). `rfid_tags.uid_encrypted` registered as an `EncryptedColumn`.
- Library choice for XLSX: none. `System.IO.Compression` reads the archive after the Java checks (200 entries, 60 MB inflated counted from real bytes, `<!DOCTYPE`/`<!ENTITY` refused across buffer boundaries, UTF-16/32 parts refused); `XmlReader` with DTDs prohibited and no resolver reads the first sheet as `fastexcel`'s `getText()` did (shared and inline strings, raw values otherwise). The template is written the same way (shared strings, bold header, widths).
- Leads: `Leads`, `DemoRequestText`, `DemoRequestGate`, `LeadRetention` (a recorded job, `lead-retention`, added to `JobsTests`' list), `LeadMetrics` (`zawed.leads.demo.requests` through `IMeterFactory`).
- Tests (128 with theory cases): `FleetTests` (16), `FleetIdentityTests` (8), `FleetImportTests` (16, with the matrix's multipart test that 1A left to 2C), `FleetGatesTests` (3), `MigrationCompatibilityTests` (the V14 half, 2), `DemoRequestTests` (43), `DemoRequestTextTests` (27), `DemoRequestGateTests` (2), `DemoRequestBehindProxyTests` (2), `LeadsIsolationTests` (5), `LeadsOpsTests` (4).
- Deviations, each kept to the Java behaviour: the demo gate is a resource filter on the handler, so Java's path gate and handler check are one piece (`DemoRequestGateTests` checks the handler and the spellings routing refuses); the plain-text, note and e-mail rules are code-point loops, since .NET regexes would refuse every astral character as two surrogates; the import worker is a `BackgroundService` outside `Jobs` (as Java's `@Scheduled` worker), listed in `ArchitectureTests.UnrecordedBackgroundServices`; the worker is off in every test application (`Zawed__FleetImport__WorkerEnabled=false`, set by the test assembly) and the tests drive it.
- Platform gap, reported, worked around in the module: `[Pattern]` passes the expression into `ValidationAttribute`'s format string, so a pattern with braces (`EgyptianContact.Mobile`, `[0-9]{8}`) throws a `FormatException` (500) when it fails. Leads uses its own `[Matches]`, which escapes them.

### Wave 3: the online fueling engine, tickets, notifications

**3A. Fueling (online)** (`zawed-deep`)

**Java to read:** `fueling/FuelingEngine`, `Fuelings`, `FuelingUsage*`, `Alerts`, `Receipts`, `Activity`, `FuelingExports`, the controllers; ADR 0018; contract `b4-fueling-contract.md`; plan §3.4; the web's `fueling.ts`, `pos.ts`.

**Endpoints:**
- POS: `POST /pos/fuelings`, `POST /pos/fuelings/{clientTxnId}/resolve`;
- receipts: `GET /receipts/{id}`;
- company: `GET /company/fuelings`, `/company/fuelings/{id}`, `/company/fuelings/export`, `/company/activity`, `/company/alerts`;
- station: `GET /station/fuelings`, `/station/fuelings/{id}`, `/station/fuelings/export`, `/station/activity`;
- Ops: `GET /ops/fuelings`, `/ops/fuelings/{id}`, `/ops/fuelings/export`, `/ops/activity`, `GET /ops/alerts`, `POST /ops/alerts/{id}/acknowledge`.

**Tables:** `fuelings`, `alerts`.

**Invariants (2, 3, 5, 6):**
- one fueling is one transaction: the row, the posting, the balance, the cooldown and the hard alerts;
- `clientTxnId` checked before and after the wallet lock, with the unique index as the last word and a concurrent duplicate re-read;
- lock order: wallet, vehicle (shared), tag;
- the validation order of PRD §6.1;
- a deadlock victim retried once (`[OwnTransactions]` plus `ITransactions.RunAsync(retryOnConflict)`);
- VIP off;
- receipt links signed with HMAC.

**Escalation to review:** `FuelingEngine` (and `Receipts`).

**Contract to publish:** the event `HardAlertRaised`; `IFuelingApi` for sync and station finance.

**Tests to port:** `FuelingTests`, `PosChannelTests` (the fueling half), `VipFuelingTests`, `WholeNumberTests`, `FuelingExportsTests`. Parallel fuelings on one wallet must neither overdraw nor double-post. Also `OnlineFuelingLoadMeasurement` as an opt-in measurement.

**Progress (2026-11-21, branch `local/dn-3a`):** done, awaiting `zawed-reviewer`.
- The 18 operations are served and out of `pending-operations.txt`; parity is green for them. Reversals are not in 3A (they are 4B's `POST /ops/fuelings/{id}/reversals`).
- `Modules/Fueling`: `FuelingEngine` (the online half of the Java engine and `resolve`), `Fuelings`, `Alerts`, `Receipts`, `Activity`, `FuelingExports`, three controllers, `FuelingSettings` (`Zawed:Fueling:VipEnabled` / `ZAWED_FUELING_VIPENABLED`, default off). Money path on Dapper with the Java statements.
- How the engine runs: each attempt is `ITransactions.RunAsync(Escalate(PLATFORM), retryOnConflict: true)` with a DI scope whose `DbSession` is attached to that transaction, so `ILedger`, `ITagGate`, `IStationDirectory`, `ICompanyDirectory`, `IPriceBook` and the event handlers all run on it. Order as Java: replay → tag → open station → wallet `FOR UPDATE` → replay again → vehicle `FOR SHARE`, tag `FOR UPDATE` → facts and `FuelingRules.Decide` → posting → row → cooldown → alerts. A unique violation (a concurrent duplicate) is answered by re-reading the stored row in a new transaction. `now` is cut to microseconds, as Java's clock and PostgreSQL keep it.
- Alerts: `LOW_BALANCE` is raised by the module's `IDomainEventHandler<WalletLowBalance>` inside the posting (no savepoint: it commits or rolls back with the money); every written alert publishes `HardAlertRaised` in the transaction (3C handles it in a savepoint).
- Contract for 3B, 4A and 4B: `IFuelingApi` — `VisibleAsync` (the Java `requireVisible("fuelings")` statement; 3B's `FuelingLinks` stand-in can switch to it), `FindAsync` (a `FuelingRef`), `UsageSinceAsync` (Java `FuelingUsage`, for the snapshot). The offline half of the engine (`recordOffline`, the exposure settings, `OfflineSync`) is left to 4A, on top of `FuelingEngine.Step`.
- Tests (40 with the load measurement): `FuelingTests` (Java's 16 plus: three pumps on one wallet, the lists, filters, cursor and RLS, PRD order with several failing checks, a 20-way clientTxnId race across two companies, ten parallel fuelings of one tag on two devices, a closed station, the rollback of a fueling that fails at its last write, the receipt rate limit, another device's clientTxnId, and the resolve test from `OfflineSyncTests`), `VipFuelingTests`, `WholeNumberTests` (all but the offline item, which needs `/pos/sync`), `FuelingExportsTests`, `FuelingContractsTests` (`HardAlertRaised` on commit only and a failing notice in its savepoint not stopping the fueling, `IFuelingApi` under RLS, the activity calendar), and `OnlineFuelingLoadMeasurement` (opt-in with `ZAWED_PERF=true`; returns at once otherwise).
- Shared changes: `PosChannelTests` (network) now expects Java's `TAG_NOT_RECOGNISED` past the PIN gate; `ArchitectureTests` gains the two escalations and the two POS actions.
- Deviation in mechanism: Npgsql 10 reads a `date` column as `DateOnly`, so the activity rows map it so.

**3B. Tickets and audit reads** (main session)

**Java to read:** `tickets/*`.

**Endpoints (12):**
- company: `GET|POST /company/tickets`, `GET /company/tickets/{id}`, `POST /company/tickets/{id}/messages`;
- station: `GET|POST /station/tickets`, `GET /station/tickets/{id}`, `POST /station/tickets/{id}/messages`;
- Ops: `GET /ops/tickets`, `GET /ops/tickets/{id}`, `POST /ops/tickets/{id}/messages|status`.

**Tables:** `tickets`, `ticket_messages`.

**Contract to publish:** the events `TicketOpened` and `TicketAnswered`.

**Tests to port:** `TicketsTests`, including the hole the IDOR sweep found: a company must not be able to link another operator's station.

**Progress (2026-11-21, branch `local/dn-3b`):** done, awaiting review.
- The 12 operations are served and out of `pending-operations.txt`; parity is green for them. Dapper with the Java statements (`Tickets`), one controller (`TicketsController`), the events `TicketOpened` and `TicketAnswered` in `Modules/Tickets/Contracts`, published in the request's transaction exactly where Java publishes them (an opened ticket; a public Operations answer, never an internal note).
- The link rule of B7 W2: a vehicle, fueling or station link is checked with `select exists (...)` in the caller's scope, so row-level security decides, and another tenant's row answers as an unknown id (`400 TICKET_LINK_NOT_FOUND`, `field`). Each portal's writes carry `Scopes` on `TICKETS_OPEN`, and Java's second check (`Not allowed for this portal`) is kept behind it.
- Stand-in: `FuelingLinks` reads `fuelings` with the Java statement until 3A publishes a contract; replace it then.
- Tests (15): `TicketsTests` (the three Java tests, plus a station's links, a fueling linked by its company and its operator and refused to the others, each portal's scope, replies to another tenant's ticket, notes and statuses, the audit rows, lists and cursors, validation codes, and row-level security as `zawed_app`), `TicketEventsTests` (the events, in the transaction, with their tenant ids). The matrix rows were already in the Java table.
- "Audit reads": `GET /admin/audit` was served by 1A; nothing else to port here.

**3C. Notifications** (main session; the savepoint rule by `zawed-deep`)

**Java to read:** `notifications/*`, contract `b7-notifications-contract.md`, the W5 findings in `HANDOVER.md`.

**Endpoints (4):** `GET /notifications`, `GET /notifications/unread-count`, `POST /notifications/read-all`, `POST /notifications/{id}/read`.

**Tables and functions:** `notifications`, `notification_reads`, `notification_read_marks`, `notification_param_keys`, `notification_params_valid`, `purge_notifications`.

**Invariants:**
- the handlers of the ledger, ticket and (once published) fueling and payout events write in a **savepoint** (`DbTx.SavepointAsync`), so a failed notification never stops its cause (W5 M1);
- the "read up to" mark (W5 M2);
- the fixed params keys.

**Job:** `NotificationRetention`.

**Tests to port:** `NotificationsTests`, `NotificationReadMarkTests`, `NotificationWriteRulesTests`. The fueling and payout notification tests come in wave 5.

### Wave 4: offline sync and station finance

**4A. Sync: snapshot and offline ingest** (`zawed-deep`)

**Java to read:** `sync/*`, `fueling/OfflineSync`, `fleet/FleetSnapshotReader`, `FleetSnapshot`, ADR 0019, ADR 0025, contract `b5-sync-contract.md`.

**Endpoints:** `GET /pos/snapshot`, `POST /pos/sync`, `GET /ops/sync/devices`, `GET /ops/sync/batches`, `GET /station/sync/devices`.

**Tables:** `sync_batches` and `fuelings` (origin OFFLINE).

**Invariants (7):**
- an offline capture is committed even when a check fails; a conflict becomes an alert, never a reversal;
- one transaction per item;
- `RETRY` on a server fault, `DUPLICATE` on a replay;
- a shortfall goes to the receivable;
- the per-device cap holds the payable;
- the company exposure alert is raised exactly once, under the wallet lock;
- `X-Zawed-Contract` answers 426;
- the snapshot carries 32-bit tag digests, rounded allowances and no PIN hashes, and checks every tag's `uid_lookup` (ADR 0027).

**Tests to port:** `OfflineSyncTests`, `SnapshotTests`, `CompanyOfflineMonitoringTests`.

**4B. Station finance** (`zawed-deep`)

**Java to read:** `stationfinance/*`, ADR 0020, contract `b6-station-finance-contract.md`.

**Endpoints (27):**
- Ops payouts: `GET /ops/payouts`, `/ops/payouts/{id}`, `POST /ops/stations/{id}/payouts`, `POST /ops/payouts/{id}/approve|cancel`;
- Ops bank accounts: `GET|POST /ops/stations/{id}/bank-account(/changes)`, `GET /ops/bank-account-changes`, `POST /ops/bank-account-changes/{id}/approve|reject`;
- Ops reversals: `POST /ops/fuelings/{id}/reversals`, `GET /ops/reversals`, `/ops/reversals/{id}`, `POST /ops/reversals/{id}/approve|reject`;
- Ops holds and statements: `GET /ops/payable-holds`, `POST /ops/fuelings/{id}/payable-hold/release`, `GET /ops/stations/{id}/payable|statement|statement/summary`;
- station portal: `GET /station/payouts`, `/station/reversals`, `/station/stations/{id}/payable|statement|statement/summary|bank-account`;
- company portal: `GET /company/reversals`.

**Tables:** `station_payables`, `payouts`, `station_bank_accounts` (encrypted, audited rekey), `fueling_reversals`, `payable_hold_releases`.

**Invariants:**
- dual control, enforced by the service and the V9 CHECK;
- the payable lock after any wallet lock;
- payable = balance − held − pending;
- one transfer pays once;
- a reversal is a mirror posting;
- the new-account hold.

**Encrypted column:** register `station_bank_accounts.account_encrypted`.

**Contract to publish:** the events `PayoutPosted` and `BankAccountChangeDecided`.

**Tests to port:** `StationFinanceTests`, `BankAccountHoldTests`.

**Progress (2026-11-21, branch `local/dn-4b`):** done, awaiting `zawed-reviewer`.
- The 27 operations are served and out of `pending-operations.txt`; parity is green for them. `recordPayout`, `requestBankAccountChange` and `requestFuelingReversal` answer 201 as Java does (the Java document lists 200).
- `Modules/StationFinance`: `BankAccounts`, `Payouts`, `PayableHolds`, `Reversals`, `StationStatements`, `PayableBook`, `StationAccess`, `StationFinanceViews`, two controllers, `StationFinanceSettings` (`Zawed:Finance:BankChangeHoldHours` / `ZAWED_FINANCE_BANKCHANGEHOLDHOURS`, default 72; `Zawed:Finance:PayoutApprovalThresholdPiastres` / `ZAWED_FINANCE_PAYOUTAPPROVALTHRESHOLDPIASTRES`, default 5,000,000; a negative value stops the boot). Dapper with the Java statements; services throw `ApiException` as the Java services do (like `TopUps`).
- Locks as Java: the request row (`FOR UPDATE`, `FOR UPDATE OF r` for a reversal), then `ILedger.LockPayableAsync`; a reversal posts through `ILedger.ReverseAsync`, which takes the wallet before the payable. The position (balance, held, pending) is one statement read under the payable lock. Unique indexes are the last line (`BANK_REFERENCE_IN_USE`, `BANK_ACCOUNT_CHANGE_PENDING`, `REVERSAL_EXISTS`, `HOLD_ALREADY_RELEASED`).
- Bank accounts: the number is read with `BankReferences.KeyAsync` (PostgreSQL NFKC, Java's fold), checked (IBAN mod 97, Egyptian length 29, or 6-34 digits), encrypted with Java's binding (`"sbac"` + the UUID big-endian, 20 bytes) and registered as an `EncryptedColumn`. A NUL or lone surrogate in a typed number becomes a space before the key, so the key is Java's (Java folds them out; `KeyAsync` would refuse them as a bank reference).
- Reversals read the fueling through `IFuelingApi.FindAsync` (the same row and scope as Java's statement); holds check a fueling's existence with `IFuelingApi.VisibleAsync`.
- Tests (37): `StationFinanceTests` (Java's 5, the station half of the maker-checker names included, plus the two events published once on commit), `BankAccountHoldTests`, `StationFinanceConcurrencyTests` (8 parallel approvals of one payout post once; parallel payouts never overbook, small and above the threshold; one bank reference typed four ways pays once and a cancelled payout frees it; a reversal requested and approved by 8 people at once posts once and is the exact mirror of the fueling; a reversal after the payout leaves the payable negative and nothing payable; held amounts never paid out under racing payouts; 8 raced releases write one row; a reversed held fueling is no longer held; a payout cannot be approved after the account changed; V9's `payouts_two_person` and `station_bank_accounts_two_person` refuse a bypass as the runtime role), `StationFinanceRulesTests` (account parsing and encryption, the binding bytes, the registered column, change control, value date, validation, audit facts, portal reads under RLS, the statement's paging and summary, which fuelings can be reversed), `BankAccountRekeyTests` (the job rekeys a key-1 account through the registered column; one `BANK_ACCOUNT_REKEYED` row, no actor, key ids only).
- Stand-in until 4A: the held offline fueling of `AHeldOfflinePayableIsNotPaidUntilOperationsReleasesIt` is written as the sync writes it (a real ledger posting plus an `OFFLINE` row with `OVER_OFFLINE_ALLOWANCE` and `payable_held`), because `/pos/sync` is 4A's. Switch it to `/pos/sync` after the merge.
- Not here: `StationFinanceNotificationsTests` (5B, on 3C's handlers of the two events); `DataKeyRotationTests` in full (5A). The Java package has no export or receivables report of its own: the station fueling export was 3A's.

**4C. (free slot)** Start wave 5's matrix rows for the modules already merged.

### Wave 5: sweeps, parity closure, real end to end

**5A. Cross-cutting security tests** (`zawed-deep`):
- finish `AuthorizationMatrixTests` (every endpoint × every role);
- port `IdorSweepTests` (171 endpoints, 422 cross-tenant cases);
- port `DataKeyRotationTests` across all registered columns;
- port `RowLevelSecurityTests` in full, and `ArchitectureTests`' notification-writer rule.

**5B. Event tests and the remaining notification triggers:** `FuelingNotificationsTests`, `StationFinanceNotificationsTests`, `NotificationFailureTests`.

**5C. Parity closure:**
- `pending-operations.txt` is empty;
- the real-API Playwright run (§4b) passes against .NET;
- `apps/api-dotnet/openapi/openapi.json` is committed and generated by a test, for the web's `api:types`;
- the CI job;
- a cutover rehearsal on a restored copy of zawed-dev (§4e, steps 1–6, on a scratch Railway database or locally).

## 4. Parity strategy

**(a) OpenAPI diff.** `OpenApiParityTests` is green after every merge (§2 has what it compares). The Java snapshot is `tests/Zawed.Tests/Fixtures/java-openapi.json`, checked against `apps/api/openapi/openapi.json`. A Java contract change during the port is copied there and ported in the same change.

**(b) The web app end to end against a real API.**
- `apps/web/playwright.live.config.ts` runs the live screens against a **stub** (`e2e/live/api-stub.ts`), so it cannot tell two APIs apart.
- The plan adds `apps/web/playwright.real.config.ts` (owner question 2 in ADR 0029). It runs the same live specs with the stub switched off, against an API URL from `E2E_API_URL`, after `apps/web/scripts/seed` has seeded it.
- It runs first against the Java API, to make the specs real, and then against the .NET API on a copy of the same database.
- Its result is part of 5C's definition of done.

**(c) Ported integration tests per module.** Every Java test class is listed under its wave. A class is ported with the same test names (PascalCase), so the two suites can be compared by name.

**(d) Money-path concurrency tests** on the real PostgreSQL, with parallel requests:
- fuelings on one wallet;
- concurrent duplicate `clientTxnId`;
- concurrent approvals of a top-up and of a payout;
- two offline syncs for one company crossing the exposure threshold;
- the import worker taken over mid-job;
- the reconciliation job on two instances;
- the migration runner on two instances (done).

**(e) The cutover runbook.** Every step needs the owner's go-ahead; nothing runs on its own.

1. **Gate.** On `dotnet-migration`:
   - `dotnet test` is green and `pending-operations.txt` is empty;
   - the real-API Playwright run is green against .NET;
   - `zawed-reviewer` has passed waves 1–5;
   - the owner has tested the .NET API on a scratch deployment (a second Railway service or a local run on a copy of the data) and said go.
2. **Freeze.** No Java deploy, and no new migration, from here until step 6 or the rollback.
3. **Back up** zawed-dev ([backup-and-restore.md](../runbooks/backup-and-restore.md)), and check the dump ends with `\unrestrict`.
4. **Switch.**
   - In `.railway/railway.ts`, change `zawed-api`'s `rootDirectory` from `apps/api` to `apps/api-dotnet` and its watch pattern to `apps/api-dotnet/**`;
   - leave every variable as it is (the .NET API reads the same ones, `SPRING_PROFILES_ACTIVE=deployed` included);
   - run `railway config plan`, read the plan, then `railway config apply`;
   - deploy from a clean worktree of the gated commit (`docs/runbooks/deploy.md`).
5. **Verify:**
   - `GET /api/v1/meta` shows `version` ending in `-dotnet` and the same `schemaVersion` as before (23, or the latest migration);
   - `/api/v1/health/readiness` and `/api/v1/health` are `UP`;
   - `select version, checksum, success from flyway_schema_history order by installed_rank desc limit 5` shows no new row (nothing to apply) or only the expected new versions, all `success = t`;
   - sign in as each role with the test accounts;
   - make one online fueling with the POS simulator, one offline sync and one top-up maker-checker;
   - the wallet statement agrees with the ledger to the piastre;
   - the next reconciliation run is `SUCCESS` with no findings.
6. **Watch** the logs for 30 minutes, and the jobs page (`/api/v1/ops/jobs`) until every job has succeeded once.
7. **Rollback**, if step 5 or 6 fails:
   - set `rootDirectory` back to `apps/api` and redeploy the last Java commit;
   - the database needs nothing, because the .NET runner writes valid Flyway history, so the Java API starts on it;
   - if a `V24+` migration was applied in between, the Java build must include it too (the two migration folders are kept identical until the Java folder is removed), or restore the backup from step 3.
8. **After a stable week**, with the owner's go-ahead:
   - remove `apps/api`;
   - point the web's `api:types` at the .NET document;
   - mark ADR 0001, 0002, 0003 and 0013 as superseded in the index.

## 5. Risks

| Risk | Guard |
|---|---|
| A subtle difference in an error code or status that the web client or the POS depends on | Port the Java tests one for one. Use the OpenAPI parity test. Run the real-API e2e against both APIs. |
| JSON differences: number and date formats, null fields, enum names | The `ZawedJson` rules. Enums declared with their wire names. Parity compares formats and enums. Per-module golden responses where the Java tests assert exact bodies. |
| A request-transaction rollback swallowing a write that must persist (lockout counters, failure audits, rate-limit hits) | Each module lists such writes, and they go through `ITransactions.RunAsync`. Sign-in tests assert the counters after the 401. |
| A scope escalation slipping in | `ArchitectureTests` keeps a reviewed list. A `zawed-reviewer` pass covers every escalating module. |
| The migration history diverging between the two folders | `MigrationRunnerTests` checks the two folders are byte-identical and the checksums equal Flyway's. |
| Parallel agents editing the same file | One folder per module. Discovery for registration, controllers and EF configurations. `pending-operations.txt` grouped by tag, where agents only delete lines. |
