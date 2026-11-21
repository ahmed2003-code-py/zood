# Working on the .NET API (`apps/api-dotnet`)

The rules of the repository's `CLAUDE.md` apply. This file adds what is specific to the .NET port. Read
[ADR 0029](../../docs/adr/0029-backend-on-aspnet-core.md) and
[the migration plan](../../docs/architecture/dotnet-migration-plan.md) first.

- **The port is literal.** The Java package and its tests are the specification: same endpoints, statuses,
  error codes, `field`s and `params`, same SQL on the money path, same lock order, same check order.
  Improvements come after parity, as separate changes.
- **The money invariants** of `.claude/agents/zawed-deep.md` hold here unchanged. Money, path, RLS, auth and
  device work goes to `zawed-deep`, and its review to `zawed-reviewer`.
- **All .NET work lives on the `dotnet-migration` branch.** Commit on a local branch; never push.
- **Toolchain:** `export PATH=$HOME/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1`. The tests
  need Docker (Testcontainers `postgres:18`), and leave no container behind.

## Where things go

| What | Where |
|---|---|
| A module (one Java package) | `src/Zawed.Api/Modules/<Name>/` |
| What another module may use: interfaces, domain events, shared records | `src/Zawed.Api/Modules/<Name>/Contracts/`. Nothing else is reachable from outside (`ArchitectureTests`). |
| The module's service registration | `Modules/<Name>/<Name>Module.cs` (`IZawedModule`), discovered automatically |
| Pure rules (money, volume, fueling rules, periods) | `src/Zawed.Domain/` (`Money`, `Volume`, `MoneyMath`, `Rules/FuelingRules`, `Rules/Periods`): no package references, no ASP.NET Core, no database |
| Shared kernel | `src/Zawed.Api/Platform/`. A module never edits it; a platform gap goes to the wave's platform owner. |
| Tests | `tests/Zawed.Tests/Modules/<Name>/`, named like the Java class they port |
| A new migration | `V<n>__<description>.sql` in **both** `apps/api/src/main/resources/db/migration` and `apps/api-dotnet/db/migration` (identical bytes; a test checks it). Never edit an applied one. |

## The conventions (playbook, adapted to ZAWED)

**Controllers** derive from `ApiControllerBase`. They are thin: a DTO in, one service call, `From(result)` out.
- Every action declares `[RequiresPermission(Permission.X)]` (add `Scopes = [...]` for a permission several portals share) or `[AllowAnonymous]`. The few signed-in-only actions are listed in `ArchitectureTests`.
- The permission runs before model binding.
- Name operations and tags as in the Java document (`[HttpGet(Name = "listCompanies")]`, `[Tags("companies")]`), and DTO records as the Java records, so `OpenApiParityTests` matches them.

**Services** return `Result<T>`. A refusal carries the Java code, message, `field` and `params`. Below a service (stores, the engine), `throw new ApiException(...)` is allowed, as in Java. A row outside the caller's scope answers `Result.NotFound(...)`, never 403.

**One transaction per request** (`TransactionFilter`), opened with the caller's RLS scope.
- Take it from `IDbSession.Current` (a `DbTx`): `tx.QueryAsync`, `tx.ExecuteAsync`, `tx.Db()` for EF Core.
- A status of 400 or more rolls everything back. A write that must survive a refusal (lockout counters, failure audits, rate-limit hits) goes through `ITransactions.RunAsync` in its own transaction.
- Per-item transactions, deadlock retries and streaming exports mark the action `[OwnTransactions]`.

**Data access.**
- EF Core (`ZawedDbContext`) for master-data CRUD and reads. Configure the entity with an `IEntityTypeConfiguration<T>` in your module, mapping the existing table and columns; never generate migrations.
- Implement `ICompanyOwned` / `IOperatorOwned` where a row belongs to one tenant: the global filter and the write guard follow.
- Use `AsNoTracking`, `Select` into DTOs and pagination in SQL (`Page<T>`, cursor = last UUIDv7).
- Dapper on `DbTx` for the money path, with the Java SQL as it is: `FOR UPDATE` in the lock order (wallet, then vehicle, then tag; wallet, then payable), unique indexes as the last line, `pg_advisory_xact_lock` via `tx.AdvisoryLockAsync` where a set of rows is decided.
- Pass enums to Dapper as `.ToString()`.

**Scope escalation** (`DbScopeContext.Escalate`) only after the code's own proof (signature, activation code, verified device and agent). Add the type, with its reason, to `ArchitectureTests.Escalations`.

**Types.**
- Money is `long` piastres and volume `long` millilitres. No `float`, `double` or `decimal` anywhere (a test checks it).
- Enums are declared with their wire and database names (`PENDING_APPROVAL`).
- Times are UTC (`DateTimeOffset` or `DateTime` of kind UTC), and the only clock is the injected `TimeProvider`.
- Ids are `Guid.CreateVersion7(clock.GetUtcNow())`.

**Validation.** Use the attributes in `Platform/Web/Validation.cs` (`NotBlank`, `Size`, `Min`, `Pattern` ...), which give the Jakarta codes. On a positional record, put them on the parameter, not on `property:`.

**Idempotency** (ADR 0022). `IdempotencyKeys.ClaimAsync` / `ClaimRequiredAsync` is called in the service, after the permission check, in the creating transaction.

**Events.**
- Publish with `IDomainEvents.PublishAsync` inside the transaction. Handle with `IDomainEventHandler<T>`, registered in your module.
- A handler that must not fail its cause (notifications) wraps its write in `tx.SavepointAsync`.
- Work that must wait for the commit uses `tx.AfterCommit`.

**Jobs.** Derive from `ScheduledJob` (interval, initial delay, `RunOnceAsync` returning a `JobResult` of counts), register it with
`PlatformSetup.AddJob<T>(services)` in your module, and read its settings with `Durations.Get` under the Java name. Keep the Java
job's own lock or lease. A test runs it once with `RunNowAsync()`.

**Rate limits.** `IRateLimiter` (in memory, per instance) for the per-minute limits; `PostgresRateLimiter.AcquireAsync(bucket,
subject, ...)` where a limit must survive a deploy and hold across instances. Both throw `RateLimitedException` (429).

**Encrypted columns** use `DataEncryptor` with the Java binding (associated data) byte for byte, and are registered for
rotation in the module: `services.AddSingleton(new EncryptedColumn("rfid_tags", "uid_encrypted", Tags.AssociatedData))`.
Lookups use `LookupHasher` with the Java namespace.

## Writing an EF Core module

`Modules/Tenancy` (companies, departments) is the reference; `Modules/Pricing` shows the same with a
money-path read. Copy their shape.

| File | What it holds |
|---|---|
| `Company.cs` | The entity (plain class, settable properties, enums with their database names) and its `IEntityTypeConfiguration<T>`: `ToTable`, `HasKey`, every column by name, `ValueGeneratedNever()` on the id. The index names the service maps to codes are constants here. |
| `CompanyDtos.cs` | Request and response records, named as the Java records. |
| `Companies.cs` | The service: `Result<T>` in and out, on the request's transaction. |
| `CompaniesController.cs` | Thin actions: permission, operation name, `[ProducesResponseType<T>(status)]`, `From(result)`. |
| `Contracts/` | What other modules may use: `ICompanyDirectory`, `CompanyCreated`. |
| `TenancyModule.cs` | `AddScoped` of the service and the contract implementations. |

**The context is the Unit of Work.** No repositories.
- Take it lazily: `private ZawedDbContext Db => session.Current.Db();` with `IDbSession session` injected.
  Never inject `ZawedDbContext` into a constructor: controllers are built before the transaction opens.
- `Db.Set<T>()`: modules do not add `DbSet`s to the shared context.
- Queries are no-tracking by default. Read with `Where` → `OrderByDescending(x => x.Id)` → `Take(size + 1)` →
  `Select(Projection)` → `Page<T>.Of(rows, size, x => x.Id)`. Keep the projection as a static `Expression`.
- A database function (`tax_id_key`) is a static stub on the entity, registered from the configuration with
  `builder.Metadata.Model.AddDbFunction(method).Name = "..."`.
- Search with `EF.Functions.Like(x.Name.ToLower(), pattern, @"\")`, the pattern escaped as `Companies.Like` does.

**Writes.**
- Create: build the entity (`Guid.CreateVersion7(clock.GetUtcNow())`, `created_*` and `updated_*`), `Db.Add`, then
  `await Db.SaveChangesAsync(ct)` **in the service**, before the audit row and before publishing an event, so
  a handler sees the row.
- Change: lock and track the row with `Db.Set<T>().FromSql($"select * from t where id = {id} for update").AsTracking()`,
  change properties, save. EF has no `FOR UPDATE`; this keeps Java's "lock, then read the before".
- A unique index is the last line: catch `DbUpdateException` with
  `DbErrors.IsUniqueViolation(ex, Configuration.IndexName)` and return the Java refusal (`TAX_ID_IN_USE`). The
  failed statement aborted the transaction; the refusal's status rolls it back.
- Answer what the Java code re-read (`select ... where id = ?`) with a no-tracking query, so timestamps come back at
  the database's precision.

**Tenancy.**
- A child row of a tenant implements `ICompanyOwned` / `IOperatorOwned`. The filter is added to every query,
  and `SaveChangesAsync` throws `CrossScopeWriteException` (404) for a row outside the scope.
- The tenant row itself (`companies`) is not `ICompanyOwned`: RLS shows a company only its own row.
- Test both layers: RLS as `zawed_app` with `Owner.AsRuntimeRoleAsync`, and the EF layer in a scoped `DbTx`
  (`MasterDataIsolationTests`).

**Money-path reads keep the Java SQL.** `IPriceBook` uses
`Db.Set<FuelPrice>().FromSql($"...").ToListAsync()` with the statement as it is. Nothing is composed around it, so
`order by` and `limit` stay as written.

**Validation and OpenAPI.**
- `[NotNull] long? X` for a required number: the absence answers `NOT_NULL`, and the schema says `required`.
- Non-nullable `string` with `[NotBlank]` for a required text.
- Checks that need the stored row (`EgyptianContact.Check`) return `Result<T>.Invalid([.. check.Issues])`.
- `[ProducesResponseType<T>(status)]` on every action, with the status the Java document lists (Java's
  `createCompany` lists 200 although a new company answers 201; `createDepartment` lists 201).

**Stand-ins.** A contract that another wave has not delivered yet is written as a small, marked stand-in inside
your own module (pricing's `StationExistsAsync` until network's `IOperatorDirectory`), never in the other module's
folder. When the owner has already committed it, copy their files byte for byte (the audit contract).

**Tests.**
- `IClassFixture<SharedApi>` (the whole application on the shared test database).
- `Owner` to arrange and inspect as the database owner; `Seed` for companies, operators and users.
- `Http.AssertErrorAsync(response, status, code)` for the envelope.
- Before the identity module lands, `ProbeApi.TokenFor(principal)` mints a token for any principal. After it, sign in through the API (`IdentityTestSupport`).
- A service or contract outside a request: open a `DbTx` with `ITransactions.BeginAsync`, `DbSession.Attach` it in a DI scope, and resolve the service (`PricesTests.InPlatformScopeAsync`).
