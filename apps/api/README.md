# ZAWED API (`apps/api`)

Spring Boot 4.1.2 modular monolith on Java 25. Decisions behind every choice here: [`docs/adr/`](../../docs/adr/README.md).
Plan and phases: [`docs/architecture/implementation-plan.md`](../../docs/architecture/implementation-plan.md).

## Requirements

- JDK 25 on `PATH` (or `JAVA_HOME`). Maven is not needed: use the wrapper `./mvnw` (`mvnw.cmd` on Windows cmd).
- Nothing else. There is no Docker and no PostgreSQL to install: tests and local runs start a real
  PostgreSQL 18 from embedded binaries (ADR 0013).

## Commands

| Command | What it does |
|---|---|
| `./mvnw verify` | Compile, all tests (unit, integration on PostgreSQL, module boundaries, OpenAPI snapshot), production jar |
| `./mvnw spring-boot:test-run` | Run the API on http://localhost:8080 with a local PostgreSQL whose data lives in `.localdb/` |
| `./mvnw test -Dtest=OpenApiSnapshotTests -Dzawed.openapi.write=true` | Refresh `openapi/openapi.json` after an intended API change, then run `npm run api:types` in `apps/web` |

Delete `.localdb/` for a clean local database.

## Endpoints today (phase B0)

| Path | Access | Purpose |
|---|---|---|
| `GET /api/v1/health`, `/health/liveness`, `/health/readiness` | public | Probes. Readiness includes the database. No details are exposed. |
| `GET /api/v1/meta` | public | Service, version, contract version, environment, server time, applied schema version |
| `GET /api/v1/openapi.json` | public | The API description the web types are generated from |
| anything else | **refused** (401) | Deny by default until authentication arrives in B1 |

## Structure

```
src/main/java/com/zawed/api/
  ZawedApiApplication.java
  platform/              shared kernel (open module)
    money/               Money, Volume, MoneyMath — integer piastres and millilitres, half-up rounding
    ids/                 UUIDv7 ids behind IdGenerator
    time/                the one Clock
    config/              ZawedProperties, validated at boot
    web/                 ApiError envelope, ApiException, request id, exception handling, OpenAPI
    security/            deny-by-default security, CORS
    meta/                GET /api/v1/meta
src/main/resources/
  application.yaml       defaults; profile "deployed" for anything on the internet
  db/migration/          Flyway migrations, forward only
src/test/java/com/zawed/api/
  support/               IntegrationTest (real HTTP + real PostgreSQL), TestDatabase
  LocalApiApplication    the local runner used by spring-boot:test-run
openapi/openapi.json     committed API description (checked by OpenApiSnapshotTests)
```

Each new module (identity, tenancy, fleet, ledger, …) is a new package directly under
`com.zawed.api`. `ModularityTests` fails the build when a module reaches into another's internals.

## Rules

- Money is `long` piastres, volume is `long` millilitres, everywhere, including SQL (`bigint`). `MoneyMath` has the same test vectors as `apps/web/src/domain/money.ts`.
- Every refusal is an `ApiException` with a stable `code`; the web picks its Arabic or English wording from the code.
- Every response carries `X-Request-Id`; every log line written during a request carries it too.
- Configuration lives in `ZawedProperties` and is validated at boot; a deployed environment refuses to start with unsafe values.

## Deployed configuration

| Variable | Meaning |
|---|---|
| `PGHOST`, `PGPORT`, `PGDATABASE`, `PGUSER`, `PGPASSWORD` | Database (Railway's PostgreSQL provides them) |
| `PORT` | HTTP port |
| `SPRING_PROFILES_ACTIVE=deployed` | JSON logs, forwarded headers |
| `ZAWED_ENVIRONMENT` | `dev`, `staging` or `production` |
| `ZAWED_CORS_ALLOWED_ORIGINS` | Comma-separated https origins of the web app |
