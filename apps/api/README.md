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

## API reference (Swagger)

Every endpoint is described, with its request and response shapes, and can be called from the browser:

| Where | Link |
|---|---|
| Local | http://localhost:8080/api/v1/docs |
| zawed-dev | https://zawed-api-production.up.railway.app/api/v1/docs |
| The description itself (OpenAPI 3.1, for tools and code generators) | `/api/v1/openapi.json` |

To call a protected endpoint from the page:

1. **Sign in.** Open `POST /api/v1/auth/login` (company and station users) or `POST /api/v1/admin/auth/login` (Operations and Super Admin), press **Try it out**, and send your email and password.
2. **Two-step verification.** If the answer has a `challengeToken`, send it with the 6-digit code from your authenticator app to `POST /api/v1/auth/mfa/verify`.
3. **Authorize.** Copy `accessToken` from the answer, press **Authorize** at the top, and paste it into `userToken`. It is valid for 15 minutes; sign in again when calls start answering `401`.
4. **POS routes** take a device token (`deviceToken`, from `POST /api/v1/pos/token`) and, for fuelings, the agent's session (`agentSession`) instead.

Each endpoint shows a lock when it needs a credential. A call is refused (`403 FORBIDDEN`) when your role may not make it; the authorization matrix is in `AuthorizationMatrixTests`.

The page is on wherever `ZAWED_API_DOCS` is not `false`. It is on for local and zawed-dev; turn it off in production until it is decided who may read the reference. The refresh and logout calls use a cookie and an origin check made for the web app, so they do not work from this page: sign in again instead.

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
