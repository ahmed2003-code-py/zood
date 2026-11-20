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

1. **Sign in.** Open `POST /api/v1/auth/login` (every role, Super Admin included), press **Try it out**, and send your email and password.
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
| `ZAWED_DATA_KEY` | Base64 of 32 random bytes: the current key that encrypts TOTP secrets, tag UIDs and bank account numbers at rest. On its own it is key 1, as before. Rotating it: [secrets-and-devices.md](../../docs/runbooks/secrets-and-devices.md), ADR 0027 |
| `ZAWED_DATA_KEY_ID` | Optional, default `1`. The current key's id (1 to 255), written as the first byte of every new value. Ids only go up from one rotation to the next. |
| `ZAWED_DATA_KEYS_PREVIOUS` | Optional, empty outside a rotation. Keys that only decrypt: `id:base64` entries separated by commas, for example `1:<old key>`. The API refuses to start with a malformed entry, an id listed twice, the current id listed here, or the same key under two ids. |
| `ZAWED_LOOKUP_KEY` | Optional while `ZAWED_DATA_KEY_ID` is 1, required after. The key of the lookup hashes (tag UIDs, activation codes, receipt links, the offline tag digest key). It never rotates with the data key: set it to the original `ZAWED_DATA_KEY` at the first rotation and never change it. The API refuses to start when it is set to anything but key 1 while key 1 is in the ring, or when its fingerprint differs from the one the database recorded at the first start (`data_key_state`). |
| `ZAWED_ENVIRONMENT` | `dev`, `staging` or `production` |
| `ZAWED_CORS_ALLOWED_ORIGINS` | Comma-separated https origins of the web app |
| `ZAWED_MFA_REQUIRED` | Optional, default `true`. Set to `false` only on `zawed-dev` while the owner tests (2026-11-19): every role then signs in with its password alone. Restore it by removing the variable. The API refuses to start in production with it off (`DeploymentGuard`). |
| `SENTRY_DSN` | Optional. Error reporting to Sentry; off when unset. `SentryScrubber` removes the request, the user, breadcrumbs, the server name, and the message of every exception that is not ours or a plain programming error. A PostgreSQL error carries the whole failing row, so its message never leaves the server. |
| `SENTRY_RELEASE` | Optional. The release the build belongs to, for example the commit. |

The web reads `VITE_SENTRY_DSN`, `VITE_SENTRY_ENVIRONMENT` and `VITE_SENTRY_RELEASE` at build time. These are Railway variables on `zawed-web`, and Railpack passes them to the build. Without a DSN the SDK is not even in the bundle; with one, it loads as a separate chunk after start-up (`src/lib/observability.ts`).
