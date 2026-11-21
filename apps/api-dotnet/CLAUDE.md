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
| Pure rules (money, volume, fueling rules, periods) | `src/Zawed.Domain/`: no package references, no ASP.NET Core, no database |
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

**Jobs.** Derive from `ScheduledJob` (interval, initial delay, `RunOnceAsync` returning a `JobResult` of counts). Keep the Java job's own lock or lease.

**Encrypted columns** use `DataEncryptor` with the Java binding (associated data) byte for byte. Lookups use `LookupHasher` with the Java namespace.

## A module, in brief

```csharp
// Modules/Pricing/PricingModule.cs
public sealed class PricingModule : IZawedModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) => services.AddScoped<Prices>();
}

// Modules/Pricing/PricesController.cs
[Route("api/v1/ops/prices"), Tags("pricing")]
public sealed class PricesController(Prices prices) : ApiControllerBase
{
    [HttpGet(Name = "listPrices"), RequiresPermission(Permission.OPS_READ)]
    public async Task<IActionResult> List([FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct) =>
        From(await prices.ListAsync(Cursors.Parse(cursor), Page<PriceEntryResponse>.Limit(limit), ct));
}

// Modules/Pricing/Prices.cs: the service, on the request's transaction
public sealed class Prices(IDbSession db, TimeProvider clock)
{
    public async Task<Result<Page<PriceEntryResponse>>> ListAsync(Guid? cursor, int limit, CancellationToken ct)
    {
        var rows = (await db.Current.QueryAsync<PriceEntryResponse>("select ... where (@cursor::uuid is null or id < @cursor) order by id desc limit @take",
            new { cursor, take = limit + 1 }, ct)).ToList();
        return Page<PriceEntryResponse>.Of(rows, limit, r => r.Id);
    }
}
```

**Tests.**
- `IClassFixture<SharedApi>` (the whole application on the shared test database).
- `Owner` to arrange and inspect as the database owner; `Seed` for companies, operators and users.
- `Http.AssertErrorAsync(response, status, code)` for the envelope.
- Before the identity module lands, `ProbeApi.TokenFor(principal)` mints a token for any principal. After it, sign in through the API (`IdentityTestSupport`).
